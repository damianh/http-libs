#:project ../.github/BuildHelpers/BuildHelpers.csproj

using static BuildHelpers.Targets;

SharedTargets(
    "forward-proxy/forward-proxy.slnf",
    "forward-proxy/src/Http.ForwardProxy");

TestTarget(Test, "forward-proxy/test/Http.ForwardProxy.Tests");

DefaultTarget(dependsOn: [Build, Test]);

await RunTargetsAndExitAsync(args);
