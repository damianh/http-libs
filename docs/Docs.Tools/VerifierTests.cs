using System.Diagnostics;
using System.Reflection;

namespace Docs.Tools;

internal static class VerifierTests
{
    public static async Task RunAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "http-libs-docs-check-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(temporary, "docs", "Docs");
        Directory.CreateDirectory(site);
        try
        {
            Write(site, "atoll.json", """{"site":"https://damianh.github.io","base":"/http-libs"}""");
            Write(site, "Content/docs/home.md", "# Home");
            Write(site, "Content/docs/topic.md", "# Topic");
            Write(site, "dist/index.html", Html("""<a href="/http-libs/docs/topic/#section">Topic</a>"""));
            Write(site, "dist/docs/topic/index.html", Html("""<h2 id="section">Section</h2><a href="../../">Home</a>"""));
            Write(site, "dist/404.html", Html("""<a href="/http-libs/">Home</a>"""));
            Write(site, "dist/site.css", "body { color: black; }");
            const string validIndex = """{"entries":[{"title":"Home","href":"/http-libs/"},{"title":"Topic","href":"/http-libs/docs/topic/"}],"generatedAt":"2026-01-01T00:00:00Z"}""";
            Write(site, "dist/search-index.json", validIndex);
            Expect(site, null);

            foreach (var (href, diagnostic) in new[]
            {
                ("/http-libs/docs/missing/", "missing target"),
                ("/http-libs/docs/topic/#missing", "missing anchor"),
                ("/docs/topic/", "exactly one"),
                ("/http-libs/http-libs/docs/topic/", "exactly one"),
                ("/http-libs/docs/Topic/", "missing target"),
                ("/http-libs/../outside/", "exactly one"),
                ("https://github.com/damianh/http-libs/blob/main/absent.cs", "GitHub source target"),
            })
            {
                Write(site, "dist/index.html", Html($"""<a href="{href}">Broken</a>"""));
                Expect(site, diagnostic);
            }
            Write(site, "dist/index.html", Html("""<img src="/http-libs/missing.png" alt="">"""));
            Expect(site, "missing target");
            Write(site, "dist/index.html", Html("""<atoll-island component-url="/scripts/missing.js"></atoll-island>"""));
            Expect(site, "exactly one");
            Write(site, "dist/index.html", Html("""<div data-index-url="/search-index.json"></div>"""));
            Expect(site, "exactly one");
            Write(site, "dist/index.html", Html("""<a href="/http-libs/docs/topic/index.html#section">Topic</a>"""));
            Expect(site, null);

            Write(site, "dist/search-index.json", """{"entries":[]}""");
            Expect(site, "results must match");
            Write(site, "dist/search-index.json", """{"entries":[{"title":"Topic","href":"/docs/topic/"}]}""");
            Expect(site, "exactly one");
            Write(site, "dist/search-index.json", "[]");
            Expect(site, "expected an object");
            Write(site, "dist/search-index.json", validIndex);
            File.Delete(Path.Combine(site, "dist", "404.html"));
            Expect(site, "Missing required output: 404.html");

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("check");
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(site);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start negative CLI check.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var diagnostics = await stdout + await stderr;
            if (process.ExitCode != 1 || !diagnostics.Contains("Missing required output: 404.html", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The checker must exit 1 with diagnostics for invalid output: {diagnostics}");
            }
            Console.WriteLine("Documentation checker fixtures passed, including nonzero CLI failure.");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void Expect(string site, string? diagnostic)
    {
        var errors = SiteVerifier.Verify(site);
        if (diagnostic is null ? errors.Count != 0 : !errors.Any(error => error.Contains(diagnostic, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Expected {diagnostic ?? "valid output"}; received: {string.Join("; ", errors)}");
        }
    }

    private static string Html(string body) =>
        $"""<!doctype html><html lang="en"><head><title>Fixture</title><link rel="stylesheet" href="/http-libs/site.css"></head><body><h1>Fixture</h1>{body}</body></html>""";

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
