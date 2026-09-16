using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if NETSTANDARD2_0 || NETFRAMEWORK
using PeriodicTimer = DamianH.HttpHybridCacheHandler.ContentStore.FileSystem.CompatibilityPeriodicTimer;
#endif

namespace DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;

/// <summary>Stores complete HTTP cache bodies using streamed writes and atomic local-file replacement.</summary>
/// <remarks>
/// Use one instance in one process per private root, on a local filesystem with atomic rename.
/// Returned readers remain caller-owned and valid across replacement, removal, cleanup, and store disposal.
/// Symbolic links and reparse points are rejected; the root and its ancestors must not be modified
/// by untrusted actors. Tags are advisory and are not persisted or used for invalidation.
/// </remarks>
public sealed class FileSystemContentStore : ILargeHttpCacheContentStore, IDisposable, IAsyncDisposable
{
    private const string Prefix = "hhc-";
    private readonly string _root;
    private readonly TimeSpan? _maximumAge;
    private readonly long? _maximumTotalBytes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileSystemContentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeLock = new();
    private readonly Task _cleanupTask;
    private Task? _disposeTask;
    private volatile bool _disposed;

    /// <summary>Creates the private namespace and starts retention cleanup when a limit is configured.</summary>
    public FileSystemContentStore(
        IOptions<FileSystemContentStoreOptions> options,
        TimeProvider timeProvider,
        ILogger<FileSystemContentStore> logger)
    {
        Guard.NotNull(options);
        Guard.NotNull(timeProvider);
        Guard.NotNull(logger);
        options.Value.Validate();
        _timeProvider = timeProvider;
        _logger = logger;
        _maximumAge = options.Value.MaximumAge;
        _maximumTotalBytes = options.Value.MaximumTotalBytes;
        _root = Path.Combine(Path.GetFullPath(options.Value.RootDirectory), "http-cache-content-v1");
        try
        {
            ValidateExistingAncestors(_root);
            Directory.CreateDirectory(_root);
            ValidateRoot();
            RemoveAbandonedTemps(CancellationToken.None);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            _logger.LogError(exception, "Failed to initialize filesystem content storage.");
            throw;
        }

        _cleanupTask = _maximumAge.HasValue || _maximumTotalBytes.HasValue
            ? RunCleanupLoopAsync(new PeriodicTimer(options.Value.CleanupInterval, _timeProvider))
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        string contentKey, Stream content, long contentLength, IEnumerable<string>? tags, CancellationToken ct)
    {
        var finalPath = GetContentPath(contentKey);
        Guard.NotNull(content);
        Guard.NotNegative(contentLength);
        if (!content.CanRead || !content.CanSeek || content.Length - content.Position != contentLength)
        {
            throw new ArgumentException("Input must be readable, seekable, and contain exactly contentLength remaining bytes.", nameof(content));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            Guard.NotDisposed(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            ValidateRoot();
            ValidateEntry(finalPath);
            tempPath = Path.Combine(_root, $"{Prefix}{Guid.NewGuid():N}.tmp");
#if NETSTANDARD2_0 || NETFRAMEWORK
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
#else
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
#endif
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var remaining = contentLength;
                    while (remaining > 0)
                    {
                        var count = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, 64 * 1024)), token).ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException("Input ended before the declared content length.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        remaining -= count;
                    }

                    if (await content.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                    {
                        throw new InvalidDataException("Input exceeds the declared content length.");
                    }

                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            token.ThrowIfCancellationRequested();
            ValidateRoot();
            var replacing = ValidateEntry(finalPath);
            File.SetLastWriteTimeUtc(tempPath, _timeProvider.GetUtcNow().UtcDateTime);
            if (replacing)
            {
                // ReplaceFile supports existing Windows readers; MoveFileEx overwrite may deny access.
                File.Replace(tempPath, finalPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
            tempPath = null;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            _logger.LogWarning(exception, "Failed to write filesystem cache content.");
            throw new HttpCacheContentStoreException("Failed to write filesystem cache content.", exception);
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDeleteOwnedFile(tempPath);
            }

            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct)
    {
        var path = GetContentPath(contentKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Guard.NotDisposed(_disposed, this);
            ct.ThrowIfCancellationRequested();
            ValidateRoot();
            ValidateEntry(path);
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            _logger.LogWarning(exception, "Failed to open filesystem cache content.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string contentKey, CancellationToken ct)
    {
        var path = GetContentPath(contentKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Guard.NotDisposed(_disposed, this);
            ct.ThrowIfCancellationRequested();
            ValidateRoot();
            ValidateEntry(path);
            File.Delete(path);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            _logger.LogWarning(exception, "Failed to remove filesystem cache content.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CleanupAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Guard.NotDisposed(_disposed, this);
            ct.ThrowIfCancellationRequested();
            ValidateRoot();
            RemoveAbandonedTemps(ct);
            if (!_maximumAge.HasValue && !_maximumTotalBytes.HasValue)
            {
                return;
            }

            var files = new List<FileInfo>();
            long total = 0;
            var now = _timeProvider.GetUtcNow();
            foreach (var path in Directory.EnumerateFiles(_root, $"{Prefix}*.body", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsOwnedName(path, ".body", 64))
                {
                    continue;
                }

                var file = new FileInfo(path);
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    continue;
                }

                if (_maximumAge is { } age && now - file.LastWriteTimeUtc >= age && TryDeleteOwnedFile(path))
                {
                    continue;
                }

                total = checked(total + file.Length);
                files.Add(file);
            }

            if (_maximumTotalBytes is { } maximum)
            {
                foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (total <= maximum)
                    {
                        break;
                    }

                    if (TryDeleteOwnedFile(file.FullName))
                    {
                        total -= file.Length;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunCleanupLoopAsync(PeriodicTimer timer)
    {
        using (timer)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await CleanupAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsFileSystemFailure(exception))
                    {
                        _logger.LogWarning(exception, "Filesystem cache retention cleanup failed; it will retry on the next interval.");
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Disposal cancels and joins this loop.
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Disposal may race a tick waiting for the operation gate.
            }
        }
    }

    private void RemoveAbandonedTemps(CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(_root, $"{Prefix}*.tmp", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (IsOwnedName(path, ".tmp", 32))
            {
                TryDeleteOwnedFile(path);
            }
        }
    }

    private bool TryDeleteOwnedFile(string path)
    {
        try
        {
            ValidateRoot();
            ValidateEntry(path);
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            _logger.LogWarning(exception, "Failed to clean up an owned filesystem cache file.");
            return false;
        }
    }

    private string GetContentPath(string key)
    {
        Guard.NotNull(key);
        return Path.Combine(_root, $"{Prefix}{Hashing.ToHex(Hashing.Sha256(Encoding.UTF8.GetBytes(key)), lowercase: true)}.body");
    }

    private static bool IsOwnedName(string path, string extension, int hashLength)
    {
        var name = Path.GetFileName(path);
        if (name.Length != Prefix.Length + hashLength + extension.Length
            || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || !name.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < Prefix.Length + hashLength; index++)
        {
            if (name[index] is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateRoot()
    {
        ValidateExistingAncestors(_root);
        var attributes = File.GetAttributes(_root);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("The content-store namespace is not a directory.");
        }
    }

    private static void ValidateExistingAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            try
            {
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Content-store paths must not contain symbolic links or reparse points.");
                }
            }
            catch (FileNotFoundException)
            {
                // New private directories are created by the constructor.
            }
            catch (DirectoryNotFoundException)
            {
                // Still examine existing ancestors before creating this directory.
            }
        }
    }

    private static bool ValidateEntry(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new IOException("Content-store entries must be regular files, not directories or links.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            // A missing single-key entry is valid.
            return false;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    /// <summary>Cancels active writes and cleanup, and waits for them to finish. Readers remain caller-owned.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _shutdown.Cancel();
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _cleanupTask.ConfigureAwait(false);
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            _gate.Release();
            _shutdown.Dispose();
        }
    }
}
