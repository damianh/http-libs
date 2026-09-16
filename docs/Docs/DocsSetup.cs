using Atoll.Lagoon.Configuration;

namespace Docs;

public static class DocsSetup
{
    public const string HomeSlug = "home";

    public static string Href(string slug) => slug == HomeSlug ? "/" : $"/docs/{slug}/";

    public static DocsConfig Config { get; } = new()
    {
        Title = "http-libs",
        Description = ".NET libraries for HTTP caching, structured field values, message signatures, and trusted-proxy forwarding.",
        BasePath = "/http-libs",
        LogoSrc = "/logo.svg",
        LogoAlt = "",
        FaviconHref = "/favicon.png",
        EnableSyntaxHighlighting = true,
        CustomCss = ["/site.css"],
        Social = [new SocialLink("GitHub", "https://github.com/damianh/http-libs", SocialIcon.GitHub)],
        Sidebar =
        [
            new() { Label = "Getting started", Link = "/docs/getting-started/" },
            new() { Label = "HTTP caching", AutoGenerate = "hybrid-cache-handler" },
            new() { Label = "File distributed cache", AutoGenerate = "file-distributed-cache" },
            new() { Label = "Structured field values", AutoGenerate = "structured-field-values" },
            new() { Label = "HTTP signatures", AutoGenerate = "signatures" },
            new() { Label = "Forwarded headers", AutoGenerate = "forwarded-headers" },
            new() { Label = "Contributing", Link = "/docs/contributing/" },
        ],
    };
}
