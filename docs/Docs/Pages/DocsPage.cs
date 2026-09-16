using Atoll.Build.Content.Collections;
using Atoll.Build.Content.Markdown;
using Atoll.Components;
using Atoll.Routing;
using Docs.Layouts;

namespace Docs.Pages;

[Layout(typeof(SiteLayout))]
[PageRoute("/docs/[...slug]")]
public sealed class DocsPage : AtollComponent, IAtollPage, IStaticPathsProvider, IPageStatusCodeProvider
{
    [Parameter(Required = true)]
    public string Slug { get; set; } = "";

    [Parameter(Required = true)]
    public CollectionQuery Query { get; set; } = null!;

    public int ResponseStatusCode { get; private set; } = 200;

    public Task<IReadOnlyList<StaticPath>> GetStaticPathsAsync() =>
        Task.FromResult<IReadOnlyList<StaticPath>>(Query.GetCollection<DocSchema>("docs")
            .Where(entry => entry.Slug != DocsSetup.HomeSlug)
            .Select(entry => new StaticPath(new Dictionary<string, string> { ["slug"] = entry.Slug }))
            .ToList());

    protected override async Task RenderCoreAsync(RenderContext context)
    {
        var entry = Query.GetEntry<DocSchema>("docs", Slug);
        if (entry is null || Slug == DocsSetup.HomeSlug)
        {
            ResponseStatusCode = 404;
            await RenderAsync(ComponentRenderer.ToFragment<NotFoundContent>());
            return;
        }

        await ContentComponent.FromRenderedContent(Query.Render(entry)).RenderAsync(context);
    }
}
