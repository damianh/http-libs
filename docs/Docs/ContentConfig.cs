using Atoll.Build.Content.Collections;
using Atoll.Build.Content.Markdown;
using Atoll.Lagoon.Components;
using Atoll.Lagoon.Islands;
using Atoll.Lagoon.Markdown;

namespace Docs;

public sealed class ContentConfig : IContentConfiguration
{
    public CollectionConfig Configure()
    {
        var markdown = DocsMarkdownRenderer.CreateMarkdownOptions(DocsSetup.Config) ?? new MarkdownOptions();
        markdown.Components = new ComponentMap()
            .Add<Aside>("aside")
            .Add<Card>("card")
            .Add<CardGrid>("card-grid")
            .Add<Steps>("steps")
            .Add<LinkCard>("link-card")
            .Add<LinkButton>("link-button")
            .Add<Tabs>("tabs")
            .Add<TabItem>("tab-item");

        var config = new CollectionConfig("Content")
            .AddCollection(ContentCollection.Define<DocSchema>("docs"));
        config.Markdown = markdown;
        return config;
    }
}
