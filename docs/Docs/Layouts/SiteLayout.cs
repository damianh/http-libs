using Atoll.Build.Content.Collections;
using Atoll.Build.Content.Markdown;
using Atoll.Components;
using Atoll.Lagoon.Navigation;
using Atoll.Slots;
using LagoonLayout = Atoll.Lagoon.Layouts.DocsLayout;

namespace Docs.Layouts;

public sealed class SiteLayout : AtollComponent
{
    [Parameter(Required = true)]
    public CollectionQuery Query { get; set; } = null!;

    [Parameter]
    public string Slug { get; set; } = "";

    [Parameter]
    public string PageTitle { get; set; } = "";

    protected override async Task RenderCoreAsync(RenderContext context)
    {
        var entry = Slug.Length == 0 ? null : Query.GetEntry<DocSchema>("docs", Slug);
        var href = Slug.Length == 0 ? "/" : DocsSetup.Href(Slug);
        var entries = Query.GetCollection<DocSchema>("docs")
            .Where(item => item.Slug != DocsSetup.HomeSlug)
            .Select(item => new SidebarEntry(item.Data.SidebarLabel ?? item.Data.Title,
                DocsSetup.Href(item.Slug), item.Slug, item.Data.Order, null))
            .ToList();
        var sidebar = new SidebarBuilder(DocsSetup.Config.Sidebar, entries).Build(href);
        var pagination = new PaginationResolver(sidebar, flatten: true).Resolve(href);
        var headings = entry is null ? (IReadOnlyList<MarkdownHeading>)[] : Query.Render(entry).Headings;
        var props = new Dictionary<string, object?>
        {
            ["Config"] = DocsSetup.Config,
            ["PageTitle"] = entry?.Data.Title ?? PageTitle,
            ["PageDescription"] = entry?.Data.Description,
            ["Headings"] = headings,
            ["SidebarItems"] = sidebar,
            ["Previous"] = pagination.Previous,
            ["Next"] = pagination.Next,
            ["BreadcrumbItems"] = new BreadcrumbBuilder(sidebar).Build(href),
        };
        await RenderAsync(ComponentRenderer.ToFragment<LagoonLayout>(props,
            SlotCollection.FromDefault(context.Slots.GetSlotFragment(SlotCollection.DefaultSlotName))));
    }
}
