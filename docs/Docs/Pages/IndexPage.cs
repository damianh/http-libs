using Atoll.Build.Content.Collections;
using Atoll.Build.Content.Markdown;
using Atoll.Components;
using Atoll.Lagoon.Components;
using Atoll.Routing;
using Docs.Layouts;

namespace Docs.Pages;

[Layout(typeof(SplashSiteLayout))]
[PageRoute("/")]
public sealed class IndexPage : AtollComponent, IAtollPage
{
    [Parameter(Required = true)]
    public CollectionQuery Query { get; set; } = null!;

    protected override async Task RenderCoreAsync(RenderContext context)
    {
        await RenderAsync(ComponentRenderer.ToFragment<Hero>(new Dictionary<string, object?>
        {
            ["Title"] = "HTTP libraries for .NET",
            ["Tagline"] = DocsSetup.Config.Description,
            ["Actions"] = (IReadOnlyList<HeroAction>)
            [
                new("Get started", "/docs/getting-started/", HeroActionVariant.Primary),
                new("View on GitHub", "https://github.com/damianh/http-libs", HeroActionVariant.Secondary),
            ],
        }));
        var entry = Query.GetEntry<DocSchema>("docs", DocsSetup.HomeSlug)
            ?? throw new InvalidOperationException("The home documentation entry is missing.");
        WriteHtml("<div class=\"prose\">");
        await ContentComponent.FromRenderedContent(Query.Render(entry)).RenderAsync(context);
        WriteHtml("</div>");
    }
}
