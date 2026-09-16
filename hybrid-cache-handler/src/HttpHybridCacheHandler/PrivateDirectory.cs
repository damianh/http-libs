// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.InteropServices;

namespace DamianH.HttpHybridCacheHandler;

internal static class PrivateDirectory
{
    public static void Create(string path)
    {
#if NET10_0_OR_GREATER
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateDirectory(path);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Apply owner-only permissions atomically, before creating any response data.
            if (Mkdir(System.Text.Encoding.UTF8.GetBytes(path + "\0"), 448) != 0)
            {
                throw new IOException($"Could not create a private cache spool directory (errno {Marshal.GetLastWin32Error()}).");
            }
        }
        else
        {
            throw new PlatformNotSupportedException("Private cache disk spooling requires Windows, Linux, or macOS.");
        }
#endif
    }

#if NETSTANDARD2_0 || NETFRAMEWORK
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir(byte[] path, uint mode);
#endif
}
