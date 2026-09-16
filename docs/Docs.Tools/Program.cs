using Docs.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--root"))
{
    Console.Error.WriteLine("Usage: Docs.Tools <check|self-test|preview> [--root <site-directory>]");
    return 2;
}

var site = Path.GetFullPath(args.Length == 3 ? args[2] : Path.Combine("docs", "Docs"));
switch (args[0])
{
    case "check":
        var errors = SiteVerifier.Verify(site);
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"Documentation checks failed: {errors.Count} error(s).");
            return 1;
        }
        Console.WriteLine("Documentation pages, anchors, assets, search, and README links are valid.");
        return 0;
    case "self-test":
        await VerifierTests.RunAsync();
        return 0;
    case "preview":
        var failures = SiteVerifier.Verify(site);
        if (failures.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
            return 1;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:4321");
        var app = builder.Build();
        using (var files = new PhysicalFileProvider(Path.Combine(site, "dist")))
        {
            app.Map(Docs.DocsSetup.Config.BasePath, branch =>
            {
                branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                branch.UseStaticFiles(new StaticFileOptions { FileProvider = files });
                branch.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(Path.Combine(site, "dist", "404.html"));
                });
            });
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
            Console.WriteLine($"Preview: http://127.0.0.1:4321{Docs.DocsSetup.Config.BasePath}/");
            await app.RunAsync();
        }
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}");
        return 2;
}
