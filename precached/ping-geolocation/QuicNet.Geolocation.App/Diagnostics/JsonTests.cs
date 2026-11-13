using System.Text.Json;
using QuicNet.Geolocation.App.Serialization;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class JsonTests
{
    public static void Run(Action<bool, string> check)
    {
        var ready = JsonSerializer.SerializeToElement(new ReadyMessage("ready", "http://127.0.0.1:1234/", "test"), AppJsonContext.Default.ReadyMessage);
        check(ready.GetProperty("kind").GetString() == "ready" && ready.GetProperty("url").GetString() == "http://127.0.0.1:1234/" &&
            ready.GetProperty("version").GetString() == "test", "generated startup JSON preserves the launcher contract");

        var empty = JsonSerializer.SerializeToElement(MapUpdate.Create(null, true), AppJsonContext.Default.MapUpdate);
        check(empty.GetProperty("reset").GetBoolean() && empty.GetProperty("heat").ValueKind == JsonValueKind.Null &&
            empty.GetProperty("markers").GetArrayLength() == 0, "generated map JSON supports the initial empty scene");

        using var fixture = typeof(JsonTests).Assembly.GetManifestResourceStream("QuicNet.Geolocation.App.Diagnostics.Fixtures.valencia-conflict.json")!;
        var update = JsonSerializer.Deserialize(fixture, JsonFiles.GetTypeInfo<LocationUpdate>())!;
        var support = new SpatialSupport(-1, 40, 1, 38, 2, 1, [0, 255], [], [], "fixture");
        var router = new RouterEvidence("192.0.2.1", new(new(39.46, -.33), "ES", "Valencia", "Valencia", "fixture"), 2, 1, 1);
        var region = update.Region! with { Representative = new(39.46, -.33), CoveringRadiusKm = 25 };
        var scene = MapUpdate.Create(update with { Region = region, Support = support, Routers = [router with { RttMs = 10 }, router] }, false);
        var json = JsonSerializer.SerializeToElement(scene, AppJsonContext.Default.MapUpdate);
        var marker = json.GetProperty("markers")[0];
        check(json.GetProperty("heat").GetProperty("values").GetBytesFromBase64().SequenceEqual(support.Values) &&
            json.GetProperty("center").GetProperty("latitude").GetDouble() == region.Representative.Latitude &&
            json.GetProperty("radius").GetDouble() == region.CoveringRadiusKm &&
            json.GetProperty("markers").GetArrayLength() == 1 && marker.GetProperty("x").GetDouble() == -.33 &&
            marker.GetProperty("y").GetDouble() == 39.46 && marker.GetProperty("label").GetString()!.Contains($"{router.RttMs:F2} ms"),
            "generated map JSON preserves heat bytes, geometry and the fastest marker per location");
        var maritime = new GeoPoint(43.8394, -4.8953);
        var fit = new LatencyPosition(maritime, 81, 6, 3, 15, true, [maritime]);
        var conditional = update with
        {
            Level = EvidenceLevel.Unknown, Support = null, Region = new("insufficient-data", null, null, 0, [], []),
            RegionalRefinement = new(region with { Status = "candidate-region" }, support, 15, 4, 3, "fixture"),
            Localities = new("regional-estimate", "Zona estimada", "estimate", "fixture", maritime, 81, fit, [], null, [])
        };
        var conditionalScene = MapUpdate.Create(conditional, false);
        check(conditionalScene.Heat is null && conditionalScene.ConditionalHeat == support && conditionalScene.Estimate is null &&
            conditionalScene.ModelPoint == maritime, "conditional heat reaches the map without promoting the maritime model point to a device location");
        var description = QuicNet.Geolocation.Workbench.LocationDescription.Describe(conditional);
        check(description.Title.Contains("sin resolver") && !description.Coordinates.Contains("43.8394"),
            "a conditional mathematical fit never becomes the main location claim");
        var reasoning = QuicNet.Geolocation.Workbench.WorkbenchExplanation.Create(conditional);
        check(reasoning.Summary.Contains("sin resolver") && reasoning.Reasons.Any(r => r.Contains("mar")) && reasoning.ModelDetail!.Contains("43.8394"),
            "reasoning explains model error and retains its coordinates for explicit technical inspection");
        using var geography = JsonDocument.Parse(Components.WorldMap.Json);
        check(geography.RootElement.GetProperty("communities").GetArrayLength() == 19 &&
            geography.RootElement.GetProperty("communities").EnumerateArray().Any(c => c.GetProperty("id").GetString() == "CT" && c.GetProperty("name").GetString() == "Cataluña"),
            "offline map packages seventeen communities and the two autonomous cities, including Catalonia");
    }
}
