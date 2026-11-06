using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using QuicNet.Geolocation.App.Components;
using QuicNet.Geolocation.App.Serialization;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--self-test-web"))
        {
            Environment.ExitCode = await Diagnostics.WebIntegrationTests.RunAsync();
            return;
        }
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode = await Diagnostics.SelfTests.RunAsync();
            return;
        }
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string dataRoot = Arg("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuicNet", "GeoLatency", "runs");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(new WorkbenchSession(dataRoot, record: false));
        var app = builder.Build();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Cookies are shared between ports. Separate workbenches must not log each other out.
        string cookieName = "GeoLatency.Access." + token[..12];
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/" && context.Request.Query["access"] == token)
            {
                context.Response.Cookies.Append(cookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
                context.Response.Redirect("/"); return;
            }
            if (context.Request.Cookies[cookieName] != token) { context.Response.StatusCode = 403; return; }
            // Browser requests must originate from this host, including WebSocket upgrades.
            if (context.Request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != $"http://{context.Request.Host}")
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapGet("/map-data", () => Results.Bytes(WorldMap.Json, "application/json"));
        app.MapGet("/localities", (double south, double west, double north, double east) =>
        {
            try { return Results.Json(LocalityCatalog.Default.InBounds(south, west, north, east), GeoJsonContext.Default.LocalityArray); }
            catch (ArgumentException) { return (IResult)Results.BadRequest(); }
        });
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        var workbench = app.Services.GetRequiredService<WorkbenchSession>();
        app.MapGet("/analysis.json", () => workbench.Preview().Update is {} update
            ? Results.File(JsonSerializer.SerializeToUtf8Bytes(update, GeoJsonContext.Default.LocationUpdate), "application/json", "quicnet-analisis.json")
            : (IResult)Results.NotFound());
        if (Arg("--session") is { } source) await workbench.LoadAsync(source);
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        string url = address + "/?access=" + token;
        Console.WriteLine(JsonSerializer.Serialize(new ReadyMessage("ready", url, "1.1.0-csharp"), AppJsonContext.Default.ReadyMessage));
        if (int.TryParse(Arg("--parent-pid"), out int parent))
            _ = WatchParentAsync(parent, app.Lifetime);
        if (args.Contains("--no-browser")) { if (!args.Contains("--keep-alive")) _ = WatchInputAsync(app.Lifetime); }
        else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        await app.WaitForShutdownAsync();
        await app.DisposeAsync();
    }
    private static async Task WatchParentAsync(int pid, IHostApplicationLifetime lifetime)
    {
        try { using var process = Process.GetProcessById(pid); await process.WaitForExitAsync(lifetime.ApplicationStopping); lifetime.StopApplication(); }
        catch (ArgumentException) { lifetime.StopApplication(); }
        catch (OperationCanceledException) { }
    }
    private static async Task WatchInputAsync(IHostApplicationLifetime lifetime)
    {
        try
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                string? line = await Console.In.ReadLineAsync(lifetime.ApplicationStopping);
                if (line is null or "stop") { lifetime.StopApplication(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
}
