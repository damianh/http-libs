using Atoll.Components;
using Atoll.Routing;
using Docs.Layouts;

namespace Docs.Pages;

[Layout(typeof(SiteLayout))]
[PageRoute("/404")]
public sealed class NotFoundPage : AtollComponent, IAtollPage, IPageStatusCodeProvider
{
    [Parameter]
    public string PageTitle { get; set; } = "Page not found";

    public int ResponseStatusCode => 404;

    protected override async Task RenderCoreAsync(RenderContext context) =>
        await RenderAsync(ComponentRenderer.ToFragment<NotFoundContent>());
}

public sealed class NotFoundContent : AtollComponent
{
    protected override Task RenderCoreAsync(RenderContext context)
    {
        WriteHtml("""
            <h1>Page not found</h1>
            <p>This documentation page could not be found. Use the navigation or search to find a library.</p>
            <p><a href="/http-libs/docs/getting-started/">Return to the documentation</a></p>
            """);
        return Task.CompletedTask;
    }
}
