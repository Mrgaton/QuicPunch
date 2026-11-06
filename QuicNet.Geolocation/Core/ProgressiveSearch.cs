using System.Net;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

public sealed record ProgressiveSearchOptions
{
    public int Rounds { get; init; } = 30;
    public int BatchSize { get; init; } = 12;
    public int Samples { get; init; } = 3;
    public int Parallelism { get; init; } = 4;
    public int TimeoutMs { get; init; } = 1000;
    public int PacketsPerSecond { get; init; } = 10;
    public int Seconds { get; init; } = 180;
    public string? Planner { get; init; }
    public bool IPv6 { get; init; }
    public bool Synthetic { get; init; }
    public EstimatorOptions Model { get; init; } = new() { ResolutionKm = 40, MaxCells = 12_000 };
    public void Validate()
    {
        if (Rounds is < 1 or > 30 || BatchSize is < 6 or > 64 || Samples is < 1 or > 20 ||
            Parallelism is < 1 or > 16 || TimeoutMs is < 100 or > 10_000 ||
            PacketsPerSecond is < 1 or > 20 || Seconds is < 1 or > 1800)
            throw new ArgumentException("Presupuesto de búsqueda fuera de rango.");
        if (Planner is not (null or "legacy" or "information" or "regional")) throw new ArgumentException("Planificador desconocido.");
        ArgumentNullException.ThrowIfNull(Model);
        Model.Validate();
    }
}

public sealed record LandmarkSelection(string LandmarkId, string Role)
{
    public double? HypotheticalRemovalFraction { get; init; }
    public string? Reason { get; init; }
}
public sealed record SearchRound(int Number, int SentPackets, MeasurementSession Session,
    LocationUpdate Update, LandmarkSelection[] Selection);
public sealed record ProgressiveSearchResult(string State, SearchRound[] Rounds);

/// <summary>Adaptive sampling only: a scheduling preference never removes a feasible geographic branch.</summary>
public static class ProgressiveSearchPlanner
{
    public const string RegionalVersion = "regional-v3.1";
    public static LandmarkSelection[] Next(Catalog catalog, MeasurementSession session,
        LocationUpdate? update, int count, int round, int samples = 3, string planner = "information", EstimatorOptions? model = null) => planner switch
    {
        "legacy" => LegacyProgressiveSearchPlanner.Next(catalog, session, update, count, round, samples),
        "information" => InformationLandmarkPlanner.Next(catalog, session, update, count, round, samples, model),
        "regional" => InformationLandmarkPlanner.Next(catalog, session, update, count, round, samples, model, regional: true),
        _ => throw new ArgumentException("Planificador desconocido.", nameof(planner))
    };
}
/// <summary>Only literal-IP ICMP to the supplied landmarks. Replay is a separate, synchronous pure calculation.</summary>
public static class ProgressiveSearch
{
    public const string EngineVersion = "netlocate-progressive-2";
    public static async Task<ProgressiveSearchResult> RunAsync(Catalog catalog, ProgressiveSearchOptions options,
        Action<SearchRound> onRound, IEchoTransport? transport = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(onRound);
        options.Validate(); catalog.Validate();
        cancellation.ThrowIfCancellationRequested();
        if (options.Synthetic && transport is null)
            throw new ArgumentException("Una simulación necesita un transporte sintético explícito.");
        var family = options.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var endpoints = catalog.Landmarks.Where(a => a.ProbeEligible && a.Kind == "anchor" &&
            IPAddress.Parse(a.Address).AddressFamily == family).ToArray();
        if (endpoints.Length == 0) throw new InvalidDataException("No hay landmarks utilizables de esta familia.");
        if (transport is null && endpoints.Any(a => !EmbeddedCatalog.IsPublic(IPAddress.Parse(a.Address))))
            throw new InvalidDataException("LIVE solo permite IP públicas literales del catálogo.");
        var session = new MeasurementSession("this-device", Guid.NewGuid().ToString("N"),
            options.IPv6 ? "ipv6" : "ipv4", []) { Synthetic = options.Synthetic };
        var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var rounds = new List<SearchRound>();
        using var probe = new NetworkProbe(new() { Samples = options.Samples, TimeoutMs = options.TimeoutMs,
            PacketsPerSecond = options.PacketsPerSecond }, transport ?? new IcmpTransport(measureElapsed: true));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        string state = "complete";
        LocationUpdate? previous = null;
        try
        {
            for (int number = 1; number <= options.Rounds; number++)
            {
                budget.Token.ThrowIfCancellationRequested();
                var plan = ProgressiveSearchPlanner.Next(catalog, session, previous, options.BatchSize, number, options.Samples, options.Planner ?? "information", options.Model);
                if (plan.Length == 0) { state = "catalog-exhausted"; break; }
                bool interrupted = false;
                foreach (var chunk in plan.Chunk(options.Parallelism))
                {
                    // Completed endpoints survive cancellation of a different endpoint in the same batch.
                    var work = await Task.WhenAll(chunk.Select(async selection =>
                    {
                        try { return await probe.MeasureAsync(selection.LandmarkId, byId[selection.LandmarkId].Address, budget.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (budget.IsCancellationRequested) { return null; }
                    })).ConfigureAwait(false);
                    foreach (var observed in work.OfType<Observation>())
                    {
                        int index = session.Observations.FindIndex(o => o.LandmarkId == observed.LandmarkId);
                        if (index < 0) session.Observations.Add(observed);
                        else
                        {
                            var old = session.Observations[index];
                            session.Observations[index] = old with { Samples = [.. old.Samples, .. observed.Samples], MeasuredUtc = observed.MeasuredUtc };
                        }
                    }
                    if (budget.IsCancellationRequested) { interrupted = true; break; }
                }
                if (session.Observations.Count > 0)
                {
                    var frozen = Freeze(session);
                    previous = Replay(catalog, frozen, options.Model, number, probe.SentPackets);
                    var snapshot = new SearchRound(number, probe.SentPackets, frozen, previous, plan);
                    rounds.Add(snapshot); onRound(snapshot);
                }
                if (interrupted) { budget.Token.ThrowIfCancellationRequested(); }
                if (previous?.Region?.Status == "inconsistent") { state = "conflicting"; break; }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { state = cancellation.IsCancellationRequested ? "cancelled" : "budget-exhausted"; }
        return new(state, rounds.ToArray());
    }

    public static LocationUpdate Replay(Catalog catalog, MeasurementSession session, EstimatorOptions model,
        int revision = 1, int sentPackets = 0)
    {
        catalog.Validate(); LatencyEstimator.ValidateSession(session);
        var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var o in session.Observations)
            if (!byId.TryGetValue(o.LandmarkId, out var anchor) ||
                !IPAddress.Parse(o.Address).Equals(IPAddress.Parse(session.Mode == "observer-to-target" ? session.TargetAddress! : anchor.Address)))
                throw new InvalidDataException("Referencia desconocida o IP distinta del catálogo: " + o.LandmarkId);
        var primaryIds = catalog.Landmarks.Where(a => a.Usable).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var primarySession = session with { Observations = session.Observations.Where(o => primaryIds.Contains(o.LandmarkId)).ToList() };
        // Validate complete frozen evidence, including declarations, before separating their role.
        var regional = DeclaredLandmarkRefiner.Evaluate(catalog, session, model);
        return new ProgressiveLocator().Replay(catalog, primarySession, model) with
        {
            Revision = revision, Phase = "progressive-round", SentPackets = sentPackets,
            MeasuredLandmarks = session.Observations.Count, RegionalRefinement = regional,
            Change = "Revisión del conjunto compatible; el color representa afinidad relativa, sin probabilidad calibrada."
        };
    }

    private static MeasurementSession Freeze(MeasurementSession session) => session with
    { Observations = session.Observations.Select(o => o with { Samples = [.. o.Samples], Route = [.. o.Route] }).ToList() };
}
