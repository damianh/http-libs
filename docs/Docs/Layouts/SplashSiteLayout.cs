using Atoll.Components;
using Atoll.Slots;
using LagoonLayout = Atoll.Lagoon.Layouts.SplashLayout;

namespace Docs.Layouts;

public sealed class SplashSiteLayout : AtollComponent
{
    protected override async Task RenderCoreAsync(RenderContext context)
    {
        await RenderAsync(ComponentRenderer.ToFragment<LagoonLayout>(
            new Dictionary<string, object?>
            {
                ["Config"] = DocsSetup.Config,
                ["PageTitle"] = "HTTP libraries for .NET",
                ["PageDescription"] = DocsSetup.Config.Description,
            },
            SlotCollection.FromDefault(context.Slots.GetSlotFragment(SlotCollection.DefaultSlotName))));
    }
}
