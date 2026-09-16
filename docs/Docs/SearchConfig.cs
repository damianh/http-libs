using Atoll.Build.Content.Collections;
using Atoll.Lagoon.Search;

namespace Docs;

public sealed class SearchConfig : ISearchIndexConfiguration
{
    public IEnumerable<SearchDocumentInput> GetDocuments(CollectionQuery query)
    {
        foreach (var entry in query.GetCollection<DocSchema>("docs"))
        {
            yield return new SearchDocumentInput(entry.Data.Title, DocsSetup.Config.BasePath + DocsSetup.Href(entry.Slug))
            {
                Description = entry.Data.Description,
                Section = entry.Data.Section,
                Topics = entry.Data.Topics,
                HtmlBody = query.Render(entry).Html,
                MaxBodyLength = int.MaxValue,
            };
        }
    }
}
