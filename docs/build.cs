#:project ../.github/BuildHelpers/BuildHelpers.csproj

using System.Runtime.CompilerServices;
using static Bullseye.Targets;
using static SimpleExec.Command;

var root = Path.GetFullPath(Path.Combine(ScriptDirectory(), ".."));
var site = Path.Combine(root, "docs", "Docs");
var tools = Path.Combine(root, "docs", "Docs.Tools", "Docs.Tools.csproj");

Task RunTools(string command) =>
    RunAsync("dotnet", $"run --project \"{tools}\" -c Release --no-build -- {command}", root);

async Task Check()
{
    await RunTools("self-test");
    await RunTools("check");
}

Target("restore", async () =>
{
    await RunAsync("dotnet", "tool restore", root);
    await RunAsync("dotnet", $"restore \"{tools}\"", root);
});

Target("compile", dependsOn: ["restore"], () =>
    RunAsync("dotnet", $"build \"{tools}\" -c Release --no-restore --nologo", root));

Target("generate", dependsOn: ["compile"], async () =>
{
    var output = Path.Combine(site, "dist");
    // Do not let a failed generator's zero exit code validate a previous artifact.
    if (Directory.Exists(output))
    {
        Directory.Delete(output, recursive: true);
    }
    await RunAsync("dotnet", $"tool run atoll build --root \"{site}\"", root);
    // Atoll emits directory URLs; Pages requires a root 404.html.
    File.Copy(Path.Combine(site, "dist", "404", "index.html"), Path.Combine(site, "dist", "404.html"), overwrite: true);
});

Target("check", dependsOn: ["compile"], Check);
Target("build", dependsOn: ["generate"], Check);
Target("preview", dependsOn: ["build"], () => RunTools("preview"));
Target("default", dependsOn: ["build"]);

await BuildHelpers.Targets.RunTargetsAndExitAsync(args);

static string ScriptDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
