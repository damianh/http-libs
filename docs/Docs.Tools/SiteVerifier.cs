using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Docs;

namespace Docs.Tools;

internal sealed partial class SiteVerifier
{
    private static readonly string[] UrlAttributes =
        ["href", "src", "component-url", "renderer-url", "before-hydration-url", "data-index-url"];
    private readonly string output;
    private readonly string repo;
    private readonly string prefix;
    private readonly Uri origin;
    private readonly HashSet<string> files;
    private readonly Dictionary<string, IDocument> pages = new(StringComparer.Ordinal);
    private readonly List<string> errors = [];
    private readonly HtmlParser parser = new();

    private SiteVerifier(string site, string basePath, Uri origin)
    {
        output = Path.Combine(site, "dist");
        repo = Path.GetFullPath(Path.Combine(site, "..", ".."));
        prefix = basePath.TrimEnd('/') + "/";
        this.origin = origin;
        files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> Verify(string site)
    {
        var configPath = Path.Combine(site, "atoll.json");
        if (!File.Exists(configPath))
        {
            return [$"{configPath}: missing site configuration."];
        }
        if (!Directory.Exists(Path.Combine(site, "dist")))
        {
            return [$"{site}: missing dist directory; build the site first."];
        }
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        var basePath = config.RootElement.GetProperty("base").GetString();
        if (basePath != DocsSetup.Config.BasePath)
        {
            return [$"{configPath}: base must match DocsSetup.Config.BasePath ({DocsSetup.Config.BasePath})."];
        }
        var siteUrl = config.RootElement.GetProperty("site").GetString();
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https")
        {
            return [$"{configPath}: site must be an absolute HTTPS URL."];
        }
        var verifier = new SiteVerifier(site, basePath, origin);
        verifier.Check(site);
        return verifier.errors;
    }

    private void Check(string site)
    {
        foreach (var path in files.Where(path => path.EndsWith(".html", StringComparison.Ordinal)))
        {
            var document = parser.ParseDocument(File.ReadAllText(DiskPath(path)));
            pages.Add(path, document);
            if (document.QuerySelectorAll("h1").Length != 1)
            {
                errors.Add($"{path}: expected exactly one article/hero h1.");
            }
            if (string.IsNullOrWhiteSpace(document.Title))
            {
                errors.Add($"{path}: missing page title.");
            }
        }

        var expectedUrls = new HashSet<string>(StringComparer.Ordinal);
        var content = Path.Combine(site, "Content", "docs");
        if (!Directory.Exists(content))
        {
            errors.Add($"{content}: missing documentation source directory.");
            return;
        }
        foreach (var path in Directory.EnumerateFiles(content, "*.md", SearchOption.AllDirectories))
        {
            var slug = Path.ChangeExtension(Path.GetRelativePath(content, path), null)
                .Replace(Path.DirectorySeparatorChar, '/');
            var href = DocsSetup.Config.BasePath + DocsSetup.Href(slug);
            expectedUrls.Add(href);
            CheckUrl(new Uri(origin, prefix), href, $"source {slug}", checkFragment: false);
        }
        RequireFile("index.html");
        RequireFile("404.html");
        if (expectedUrls.Count < 2)
        {
            errors.Add("Expected a landing page and documentation in the source collection.");
        }

        foreach (var (path, document) in pages)
        {
            var pageUrl = new Uri(origin, prefix + (path.EndsWith("index.html", StringComparison.Ordinal)
                ? path[..^"index.html".Length] : path));
            CheckDocument(document, pageUrl, path);
        }
        foreach (var path in files.Where(path => path.EndsWith(".css", StringComparison.Ordinal)))
        {
            foreach (Match match in CssUrl().Matches(File.ReadAllText(DiskPath(path))))
            {
                CheckUrl(new Uri(origin, prefix + path), match.Groups[1].Value.Trim(' ', '\'', '"'), path);
            }
        }

        CheckSearch(expectedUrls);
        CheckReadmes();
    }

    private void CheckDocument(IDocument document, Uri pageUrl, string source)
    {
        foreach (var element in document.QuerySelectorAll(string.Join(", ", UrlAttributes.Select(attribute => $"[{attribute}]"))))
        {
            foreach (var attribute in UrlAttributes)
            {
                if (element.GetAttribute(attribute) is { } value)
                {
                    CheckUrl(pageUrl, value, source);
                }
            }
        }
    }

    private void CheckUrl(Uri pageUrl, string href, string source, bool checkFragment = true)
    {
        if (!Uri.TryCreate(pageUrl, href, out var target))
        {
            errors.Add($"{source}: invalid URL '{href}'.");
            return;
        }
        if (target.Scheme is "data" or "mailto" or "tel")
        {
            return;
        }
        if (target.Scheme is not ("https" or "http"))
        {
            errors.Add($"{source}: unsupported URL scheme in '{href}'.");
            return;
        }
        if (target.Host != origin.Host)
        {
            CheckSourceLink(target, source);
            return;
        }
        var path = Uri.UnescapeDataString(target.AbsolutePath);
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || path[prefix.Length..].StartsWith(prefix.TrimStart('/'), StringComparison.Ordinal))
        {
            errors.Add($"{source}: URL must have exactly one {prefix} prefix: '{href}'.");
            return;
        }
        var relative = path[prefix.Length..];
        if (relative.Contains('\\') || relative.Split('/').Contains(".."))
        {
            errors.Add($"{source}: invalid local path '{href}'.");
            return;
        }
        var file = files.Contains(relative) ? relative : relative.TrimEnd('/') + "/index.html";
        if (relative.Length == 0)
        {
            file = "index.html";
        }
        if (!files.Contains(file))
        {
            errors.Add($"{source}: missing target '{href}' (expected {file}).");
            return;
        }
        if (checkFragment && target.Fragment.Length > 1 && pages.TryGetValue(file, out var document))
        {
            var id = Uri.UnescapeDataString(target.Fragment[1..]);
            if (document.GetElementById(id) is null
                && !document.QuerySelectorAll("a[name]").Any(anchor => anchor.GetAttribute("name") == id))
            {
                errors.Add($"{source}: missing anchor '{href}'.");
            }
        }
    }

    private void CheckSourceLink(Uri target, string source)
    {
        if (target.Host != "github.com")
        {
            return;
        }
        var segments = target.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 5 || segments[0] != "damianh" || segments[1] != "http-libs"
            || segments[2] is not ("blob" or "tree") || segments[3] != "main")
        {
            return;
        }
        var relative = Uri.UnescapeDataString(string.Join('/', segments.Skip(4)));
        var path = Path.GetFullPath(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || (!File.Exists(path) && !Directory.Exists(path)))
        {
            errors.Add($"{source}: GitHub source target does not exist: {target}.");
        }
    }

    private void CheckSearch(HashSet<string> expectedUrls)
    {
        if (!RequireFile("search-index.json"))
        {
            return;
        }
        using var index = JsonDocument.Parse(File.ReadAllText(DiskPath("search-index.json")));
        if (index.RootElement.ValueKind != JsonValueKind.Object
            || !index.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            errors.Add("search-index.json: expected an object with an entries array.");
            return;
        }
        var indexedUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.EnumerateArray())
        {
            if (!item.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(title.GetString()))
            {
                errors.Add("search-index.json: each entry must have a title and href.");
                continue;
            }
            var url = href.GetString()!;
            if (!indexedUrls.Add(url))
            {
                errors.Add($"search-index.json: duplicate result '{url}'.");
            }
            CheckUrl(new Uri(origin, prefix), url, "search-index.json");
        }
        if (!expectedUrls.SetEquals(indexedUrls))
        {
            errors.Add("search-index.json: results must match all published content pages, excluding the 404 page.");
        }
    }

    private void CheckReadmes()
    {
        var sourceReadmes = new List<string> { Path.Combine(repo, "README.md") };
        foreach (var directory in new[] { "hybrid-cache-handler", "file-distributed-cache", "structured-field-values", "signatures", "forwarded-headers" })
        {
            var path = Path.Combine(repo, directory);
            if (Directory.Exists(path))
            {
                sourceReadmes.AddRange(Directory.EnumerateFiles(path, "README.md", SearchOption.AllDirectories)
                    .Where(file => !Path.GetRelativePath(path, file).Split(Path.DirectorySeparatorChar)
                        .Any(part => part is "bin" or "obj" or "artifacts" or "node_modules")));
            }
        }
        foreach (var path in sourceReadmes.Where(File.Exists))
        {
            var document = parser.ParseDocument(Markdig.Markdown.ToHtml(File.ReadAllText(path)));
            var source = Path.GetRelativePath(repo, path);
            foreach (var link in document.QuerySelectorAll("a[href]"))
            {
                var href = link.GetAttribute("href")!;
                if (Uri.TryCreate(href, UriKind.Absolute, out var target)
                    && target.Scheme is "http" or "https")
                {
                    CheckUrl(new Uri(origin, prefix), href, source);
                }
                else if (href.StartsWith("/docs/", StringComparison.Ordinal)
                    || href.StartsWith(prefix, StringComparison.Ordinal))
                {
                    errors.Add($"{source}: documentation links in READMEs must be absolute: '{href}'.");
                }
            }
        }
    }

    private bool RequireFile(string file)
    {
        if (files.Contains(file))
        {
            return true;
        }
        errors.Add($"Missing required output: {file}.");
        return false;
    }

    private string DiskPath(string relative) => Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex(@"url\(\s*([^)]*?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();
}
