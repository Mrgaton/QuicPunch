using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class HeatmapReport
{
    private static readonly Lazy<string> Land = new(ReadLand);
    private static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string D(double value) => value.ToString("N0", CultureInfo.GetCultureInfo("es-ES"));
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static double Latitude(double mercatorY) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * mercatorY))) * 180 / Math.PI;
    private static double MercatorY(double lat) => (1 - Math.Asinh(Math.Tan(Math.Clamp(lat, -85.05112878, 85.05112878) * Math.PI / 180)) / Math.PI) / 2;

    public static string Render(RecordingHeader header, ProgressiveSearchResult result, bool running = false, bool replay = false)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">");
        if (running) html.Append("<meta http-equiv=\"refresh\" content=\"3\">");
        html.Append("<title>NetLocate · búsqueda progresiva</title><style>");
        html.Append(Css);
        foreach (var r in result.Rounds)
            html.Append("#r").Append(r.Number).Append(":checked~.snapshots>#frame").Append(r.Number).Append("{display:block}#r")
                .Append(r.Number).Append(":checked~nav label[for=r").Append(r.Number).Append("]{background:#faf5de;color:#101827}");
        html.Append("</style></head><body><header><div class=\"eyebrow\">NETLOCATE / MEDICIONES DE RED</div><h1>Acercarse, medir, volver a comprobar.</h1><p>Exploración global, búsqueda local y comprobaciones en la periferia. Todos los cálculos y mapas se generan en este equipo.</p><div class=\"badges\"><b>")
            .Append(header.Options.Synthetic ? "SIMULACIÓN · DATOS SINTÉTICOS" : replay ? "REPLAY · SIN RED" : "LIVE · ICMP A LANDMARKS")
            .Append("</b><span>").Append(E(result.State)).Append("</span><span>").Append(header.Catalog.Landmarks.Count).Append(" referencias locales</span><span>Plan: ").Append(E(header.Options.Planner ?? "legacy")).Append("</span></div></header><main>");
        html.Append("<aside><strong>El color muestra afinidad relativa, no probabilidad.</strong> La zona ámbar conserva las posiciones compatibles con las cotas del modelo. El centro geométrico no confirma dónde está el ordenador. No se ofrece un «95 %» sin calibración.</aside>");
        if (header.RefinedFromSha256 is { } parent)
            html.Append("<aside>Refinamiento de malla SIN MEDICIONES NUEVAS: mismas muestras y cotas físicas, mayor presupuesto de celdas. No demuestra mayor precisión geográfica. Registro de origen SHA-256: <code style=\"overflow-wrap:anywhere\">").Append(E(parent)).Append("</code></aside>");
        if (result.Rounds.Any(r => r.Session.Observations.Any(o => o.Samples.Any(s => s.TimingMethod == "elapsed-upper-v1"))))
            html.Append("<p class=\"muted\">Cronometraje fraccional: duración completa de envío y respuesta, incluido el sobrecoste local; sin restar acceso. Margen nominal de reloj de al menos 0,05 ms. Las muestras antiguas conservan su margen de cuantización. Los puntos lilas son ubicaciones declaradas, sin verificar.</p>");
        if (header.Options.Synthetic)
            html.Append("<aside class=\"synthetic\">Los tiempos de esta demostración son simulados. La reducción del área no demuestra precisión en una conexión real.</aside>");
        if (running) html.Append("<p class=\"muted\">Actualización automática cada 3 segundos mientras dura la medición. Puedes abrir este archivo directamente en el navegador.</p>");
        if (result.Rounds.Length == 0)
        {
            html.Append("<section class=\"panel\"><h2>Esperando la primera ronda</h2><p>Aún no hay mediciones suficientes para dibujar una zona.</p></section>");
        }
        else
        {
            foreach (var r in result.Rounds)
                html.Append("<input class=\"round\" type=\"radio\" name=\"round\" id=\"r").Append(r.Number).Append('"')
                    .Append(r.Number == result.Rounds[^1].Number ? " checked" : "").Append('>');
            html.Append("<nav aria-label=\"Revisiones de la búsqueda\">");
            foreach (var r in result.Rounds)
                html.Append("<label tabindex=\"0\" for=\"r").Append(r.Number).Append("\">Ronda ").Append(r.Number).Append("</label>");
            html.Append("</nav><svg width=\"0\" height=\"0\" aria-hidden=\"true\" class=\"definitions\"><defs><g id=\"land\">").Append(Land.Value).Append("</g>");
            foreach (var r in result.Rounds)
            {
                html.Append("<g id=\"layer").Append(r.Number).Append("\">").Append(Layer(r.Update)).Append("</g>");
                if (r.Update.RegionalRefinement is { } regional)
                    html.Append("<g id=\"regional-layer").Append(r.Number).Append("\">")
                        .Append(Layer(r.Update with { Region = regional.Region, Support = regional.Support })).Append("</g>");
            }
            html.Append("</defs></svg><div class=\"snapshots\">");
            foreach (var r in result.Rounds)
            {
                var region = r.Update.Region;
                string status = region?.Status ?? "insufficient-data";
                html.Append("<section id=\"frame").Append(r.Number).Append("\" class=\"frame\"><div class=\"metrics\">");
                Metric(html, "Área compatible", region is { Cells.Count: > 0 } ? D(region.AreaKm2) + " km²" : "Sin región válida");
                Metric(html, "Radio de cobertura", region?.CoveringRadiusKm is { } radius ? D(radius) + " km" : "Sin determinar");
                Metric(html, "Referencias que responden", r.Session.Observations.Count(o => o.MinimumRttMs.HasValue).ToString(CultureInfo.InvariantCulture));
                Metric(html, "Paquetes enviados", r.SentPackets.ToString(CultureInfo.InvariantCulture));
                html.Append("</div><div class=\"map-heading\"><h2>Ronda ").Append(r.Number).Append("</h2><span>").Append(E(status)).Append("</span></div>");
                html.Append("<div class=\"maps\"><section class=\"panel\"><h3>Vista global · todas las regiones</h3>")
                    .Append(Svg(header.Catalog, r, false)).Append("</section><section class=\"panel\"><h3>Detalle de las señales más rápidas</h3>")
                    .Append(Svg(header.Catalog, r, true)).Append("</section></div>");
                html.Append("<div class=\"legend\"><span class=\"gradient\"></span> Menor → mayor afinidad <span class=\"amber\"></span> Zona compatible <span>● Landmark medido &nbsp; + Centro geométrico</span></div>");
                html.Append("<p class=\"muted\">Radio que cubre las celdas retenidas, condicionado al catálogo y al modelo; no es un error medido ni un intervalo estadístico. ")
                    .Append("La malla puede conservar regiones separadas o ampliarse al cambiar su resolución. El detalle visual no recorta la búsqueda global.</p>");
                var tightest = region?.Constraints.OrderBy(c => c.RadiusKm).FirstOrDefault();
                int notContained = MeasurementAudit.NonContained(region?.Constraints ?? []).Count;
                html.Append("<p class=\"muted\">Cotas no contenidas en otra: <strong>").Append(notContained)
                    .Append(" de ").Append(region?.Constraints.Count ?? 0).Append("</strong>.");
                if (tightest is not null)
                    html.Append(" La cota más corta aún permite ").Append(D(tightest.RadiusKm)).Append(" km desde su referencia (RTT ")
                        .Append(N(tightest.RttMs)).Append(" ms). Un píxel más pequeño no elimina ese margen.");
                html.Append("</p>");
                if (region?.Representative is { } point)
                    html.Append("<p>Centro geométrico: ").Append(N(point.Latitude)).Append("°, ").Append(N(point.Longitude)).Append("°. Su distancia a una ciudad no valida la ubicación.</p>");
                if (r.Update.RegionalRefinement is { } refinement)
                {
                    html.Append("<section class=\"panel conditional\"><h3>Hipótesis con sondas declaradas · separada de la región principal</h3><p>")
                        .Append(E(refinement.Explanation)).Append("</p><div class=\"metrics\">");
                    Metric(html, "Área condicional", refinement.Region.Cells.Count > 0 ? D(refinement.Region.AreaKm2) + " km²" : "Incompatible / sin región");
                    Metric(html, "Radio condicional", refinement.Region.CoveringRadiusKm is { } cr ? D(cr) + " km" : "Sin determinar");
                    Metric(html, "Sondas declaradas / ASN", refinement.RespondingSites + " / " + refinement.NetworkGroups);
                    Metric(html, "RTT mínimo declarado", N(refinement.MinimumRttMs) + " ms");
                    html.Append("</div>");
                    if (refinement.Region.Cells.Count > 0)
                        html.Append(Svg(header.Catalog, r, true, conditional: true));
                    html.Append("<p>Estado: ").Append(E(refinement.Region.Status))
                        .Append(". Una zona pequeña condicionada a declaraciones no demuestra precisión de ciudad.");
                    if (refinement.Region.Cells.Count > 0)
                        html.Append(" Objetivo de malla: ").Append(N(header.Options.Model.ResolutionKm)).Append(" km; máximo radio de celda retenida: ")
                            .Append(N(refinement.Region.Cells.Max(c => c.CoverRadiusKm))).Append(" km. El presupuesto puede impedir alcanzar el objetivo.");
                    html.Append("</p></section>");
                }
                html.Append("<details><summary>Selección de esta ronda y mediciones</summary><table><thead><tr><th>Landmark / IP literal</th><th>Selección</th><th>RTT mínimo</th><th>Éxitos / intentos</th></tr></thead><tbody>");
                var observations = r.Session.Observations.ToDictionary(o => o.LandmarkId, StringComparer.Ordinal);
                foreach (var s in r.Selection)
                {
                    var landmark = header.Catalog.Landmarks.First(a => a.Id == s.LandmarkId);
                    observations.TryGetValue(s.LandmarkId, out var o);
                    html.Append("<tr><td>").Append(E(string.IsNullOrWhiteSpace(landmark.Name) ? landmark.Id : landmark.Name)).Append("<small>").Append(E(landmark.Address))
                        .Append(landmark.Asn is { } asn ? " · AS" + asn : "").Append(landmark.CoordinateEvidence == "operator-declared" ? " · coordenadas declaradas; solo hipótesis" : " · referencia principal").Append("</small></td><td>").Append(E(s.Role)).Append("<small>").Append(E(s.Reason)).Append(s.HypotheticalRemovalFraction is { } gain ? " Corte hipotético: " + D(gain * 100) + " %; no es una reducción observada ni una probabilidad." : "").Append("</small></td><td>").Append(o?.MinimumRttMs is { } rtt ? N(rtt) + " ms" : "Sin respuesta")
                        .Append("</td><td>").Append(o?.Samples.Count(v => v.Status == "Success") ?? 0).Append(" / ").Append(o?.Samples.Count ?? 0).Append("</td></tr>");
                }
                html.Append("</tbody></table></details><details><summary>Supuestos y advertencias</summary><ul>");
                foreach (string note in r.Update.Notes) html.Append("<li>").Append(E(note)).Append("</li>");
                html.Append("<li>Las respuestas de un destino no autentican su identidad ni certifican sus coordenadas. VPN y túneles pueden desplazar las señales hacia la salida de red.</li></ul></details></section>");
            }
            html.Append("</div><section class=\"panel history\"><h2>Evolución completa</h2><table><thead><tr><th>Ronda</th><th>Área compatible (km²)</th><th>Radio (km)</th><th>Información + revisión / frontera / global</th></tr></thead><tbody>");
            foreach (var r in result.Rounds)
                html.Append("<tr><td>").Append(r.Number).Append("</td><td>").Append(r.Update.Region is { Cells.Count: > 0 } rr ? D(rr.AreaKm2) : "—")
                    .Append("</td><td>").Append(r.Update.Region?.CoveringRadiusKm is { } radius ? D(radius) : "—").Append("</td><td>")
                    .Append(r.Selection.Count(s => s.Role is "local" or "information" or "verify" or "anchor" or "regional")).Append(" / ").Append(r.Selection.Count(s => s.Role is "periphery" or "boundary")).Append(" / ")
                    .Append(r.Selection.Count(s => s.Role == "global")).Append("</td></tr>");
            html.Append("</tbody></table></section>");
        }
        html.Append("<footer><p>Motor ").Append(E(header.EngineVersion)).Append(" · Catálogo SHA-256 <code>").Append(E(header.CatalogSha256))
            .Append("</code></p><p>Referencias: RIPE Atlas / RIPE NCC. ASN locales: <a href=\"https://db-ip.com/\">DB-IP</a> (CC BY 4.0). Cartografía Natural Earth, dominio público. Sin teselas, fuentes, scripts ni recursos externos.</p><details><summary>Procedencia y condiciones de los datos incorporados</summary><pre>")
            .Append(E(EmbeddedCatalog.Default.Notice)).Append("</pre></details></footer></main></body></html>");
        return html.ToString();
    }

    private static void Metric(StringBuilder output, string label, string value) =>
        output.Append("<div class=\"metric\"><span>").Append(E(label)).Append("</span><strong>").Append(E(value)).Append("</strong></div>");

    private static string Layer(LocationUpdate update)
    {
        var s = new StringBuilder("<use href=\"#land\"/>");
        for (int lon = -180; lon <= 180; lon += 30)
            s.Append("<path class=\"grid\" d=\"M").Append(lon + 180).Append(" 0V180\"/>");
        for (int lat = -60; lat <= 60; lat += 30)
            s.Append("<path class=\"grid\" d=\"M0 ").Append(90 - lat).Append("H360\"/>");
        if (update.Region is { } region)
        {
            // Coalesce adjacent cells on each latitude band; preserve all branches, including polar cells.
            foreach (var band in region.Cells.GroupBy(c => (c.South, c.North)))
            {
                double? west = null; double east = 0;
                foreach (var cell in band.OrderBy(c => c.West))
                {
                    if (west is not null && Math.Abs(cell.West - east) > 1e-8)
                    { Rectangle(s, west.Value + 180, 90 - band.Key.North, east - west.Value, band.Key.North - band.Key.South, "#fbbf24", ".18"); west = null; }
                    west ??= cell.West; east = cell.East;
                }
                if (west is not null) Rectangle(s, west.Value + 180, 90 - band.Key.North, east - west.Value, band.Key.North - band.Key.South, "#fbbf24", ".18");
            }
        }
        if (update.Support is { } heat)
        {
            double top = MercatorY(heat.North), bottom = MercatorY(heat.South);
            for (int y = 0; y < heat.Height; y++)
            {
                double north = Latitude(top + (bottom - top) * y / heat.Height);
                double south = Latitude(top + (bottom - top) * (y + 1) / heat.Height);
                for (int x = 0; x < heat.Width;)
                {
                    int bin = heat.Values[y * heat.Width + x] / 16;
                    bool empty = heat.Values[y * heat.Width + x] == 0;
                    int end = x + 1;
                    while (end < heat.Width && heat.Values[y * heat.Width + end] / 16 == bin &&
                        (heat.Values[y * heat.Width + end] == 0) == empty) end++;
                    if (!empty)
                        Rectangle(s, 180 + heat.West + (heat.East - heat.West) * x / heat.Width, 90 - north,
                            (heat.East - heat.West) * (end - x) / heat.Width, north - south,
                            "hsl(" + N(220 - bin / 15.0 * 200) + " 90% 55%)", ".65");
                    x = end;
                }
            }
        }
        return s.ToString();
    }

    private static void Rectangle(StringBuilder s, double x, double y, double width, double height, string color, string opacity) =>
        s.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(width))
            .Append("\" height=\"").Append(N(height)).Append("\" fill=\"").Append(color).Append("\" opacity=\"").Append(opacity).Append("\"/>");

    private static string Svg(Catalog catalog, SearchRound round, bool zoom, bool conditional = false)
    {
        var update = round.Update;
        if (conditional && update.RegionalRefinement is { } refinement)
            update = update with { Region = refinement.Region, Support = refinement.Support };
        var focus = update.Support?.Peaks.FirstOrDefault()?.Position ?? update.Region?.Representative;
        double width = 360, height = 180, x = 0, y = 0;
        if (zoom && focus is not null)
        {
            double scale = update.Support?.Peaks.FirstOrDefault()?.ScaleKm ?? update.Region?.CoveringRadiusKm ?? 3000;
            height = Math.Clamp(scale / 111 * 6, 8, 140);
            width = Math.Min(360, height * 2);
            x = focus.Longitude + 180 - width / 2;
            y = Math.Clamp(90 - focus.Latitude - height / 2, 0, 180 - height);
        }
        var s = new StringBuilder("<svg role=\"img\" aria-label=\"Mapa de afinidad y región compatible\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"");
        s.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append("\"><rect x=\"")
            .Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height)).Append("\" fill=\"#111f32\"/>");
        foreach (int shift in new[] { -360, 0, 360 })
        {
            if (shift == -360 && x >= 0 || shift == 360 && x + width <= 360) continue;
            s.Append("<g transform=\"translate(").Append(shift).Append(" 0)\"><use href=\"#").Append(conditional ? "regional-layer" : "layer").Append(round.Number).Append("\"/>");
            foreach (var o in round.Session.Observations)
            {
                var a = catalog.Landmarks.First(l => l.Id == o.LandmarkId);
                s.Append("<circle cx=\"").Append(N(a.Position.Longitude + 180)).Append("\" cy=\"").Append(N(90 - a.Position.Latitude))
                    .Append("\" r=\"").Append(N(width / 450)).Append("\" fill=\"").Append(o.MinimumRttMs.HasValue ? a.CoordinateEvidence == "operator-declared" ? "#d8b4fe" : "#fff8dc" : "#687587")
                    .Append("\"><title>").Append(E(a.Id)).Append(" · ").Append(E(a.Address)).Append(" · ")
                    .Append(o.MinimumRttMs is { } rtt ? N(rtt) + " ms" : "Sin respuesta").Append("</title></circle>");
            }
            if (update.Region?.Representative is { } p)
            {
                double px = p.Longitude + 180, py = 90 - p.Latitude, arm = width / 130;
                s.Append("<path stroke=\"#ffffff\" fill=\"none\" vector-effect=\"non-scaling-stroke\" stroke-width=\"2\" d=\"M")
                    .Append(N(px - arm)).Append(' ').Append(N(py)).Append('H').Append(N(px + arm)).Append('M').Append(N(px)).Append(' ')
                    .Append(N(py - arm)).Append('V').Append(N(py + arm)).Append("\"><title>Centro geométrico, no ubicación confirmada</title></path>");
            }
            s.Append("</g>");
        }
        s.Append("</svg>");
        return s.ToString();
    }

    private static string ReadLand()
    {
        using var source = typeof(HeatmapReport).Assembly.GetManifestResourceStream("NetLocate.Map.gz")!;
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var output = new StringBuilder();
        foreach (var country in document.RootElement.GetProperty("countries").EnumerateArray())
        {
            var coordinates = country.GetProperty("coordinates");
            var polygons = country.GetProperty("type").GetString() == "Polygon" ? new[] { coordinates } : coordinates.EnumerateArray().ToArray();
            foreach (var polygon in polygons)
            {
                var path = new StringBuilder();
                foreach (var ring in polygon.EnumerateArray())
                {
                    double lastX = double.NaN, lastY = double.NaN; bool first = true;
                    foreach (var point in ring.EnumerateArray())
                    {
                        double px = point[0].GetDouble() + 180, py = 90 - point[1].GetDouble();
                        if (!first && Math.Abs(px - lastX) + Math.Abs(py - lastY) < .12) continue;
                        path.Append(first ? 'M' : 'L').Append(N(px)).Append(' ').Append(N(py));
                        first = false; lastX = px; lastY = py;
                    }
                    if (!first) path.Append('Z');
                }
                output.Append("<path class=\"land\" fill-rule=\"evenodd\" d=\"").Append(path).Append("\"/>");
            }
        }
        return output.ToString();
    }

    private const string Css = """
        :root{color-scheme:dark;font-family:Segoe UI,system-ui,sans-serif;background:#0b1220;color:#e6edf5}
        *{box-sizing:border-box}body{margin:0}header,main{max-width:1440px;margin:auto;padding:32px}
        header{padding-top:48px;padding-bottom:12px}.eyebrow{font-size:12px;letter-spacing:.16em;color:#69d3c5}
        h1{font-size:clamp(28px,4vw,48px);font-weight:600;letter-spacing:-.04em;margin:12px 0}
        p{line-height:1.6}header p,.muted{color:#a7b5c6;font-size:14px}.badges,nav,.legend{display:flex;flex-wrap:wrap;gap:12px;align-items:center}
        .badges b,.badges span{border:1px solid #2b3c51;border-radius:20px;padding:7px 12px;font-size:12px}.badges b{color:#8be3d5}
        aside{border-left:3px solid #fbbf24;background:#182335;padding:16px 20px;margin-bottom:16px;font-size:14px;line-height:1.6}
        .synthetic{border-color:#fb923c;color:#ffcf9e}.round{position:absolute;opacity:0;pointer-events:none}
        nav{margin:24px 0}nav label{cursor:pointer;border:1px solid #35455b;border-radius:6px;padding:9px 14px;font-size:13px}
        nav label:focus-visible{outline:2px solid #69d3c5}.frame{display:none}.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}
        .metric{background:#142034;border:1px solid #25364c;border-radius:10px;padding:18px}.metric span{display:block;color:#a7b5c6;font-size:12px}
        .metric strong{display:block;font-size:clamp(17px,2vw,25px);margin-top:8px;font-weight:600;overflow-wrap:anywhere}
        .map-heading{display:flex;align-items:center;justify-content:space-between}.map-heading span{color:#a7b5c6;font-size:12px}
        h2{font-size:20px}h3{font-size:13px;font-weight:500;color:#bfcddd;margin:0 0 12px}
        .maps{display:grid;grid-template-columns:1fr 1fr;gap:16px}.panel{background:#111d2e;border:1px solid #27364b;border-radius:12px;padding:16px}
        .maps svg{display:block;width:100%;aspect-ratio:2/1;border-radius:6px}.land{fill:#293b4e;stroke:#40546b;stroke-width:.035}
        .conditional{margin:24px 0;border-color:#79638b}.conditional>svg{display:block;width:100%;aspect-ratio:2/1;max-height:560px;border-radius:6px;margin-top:16px}
        .grid{stroke:#8fa6bf;stroke-width:.04;opacity:.2;fill:none}.legend{font-size:12px;color:#bfcddd;margin:16px 0}
        .gradient{width:120px;height:8px;border-radius:5px;background:linear-gradient(90deg,#2363f5,#22d3c2,#f39d25)}
        .amber{width:14px;height:14px;background:#fbbf2440;border:1px solid #fbbf24}details{margin:14px 0;font-size:13px;overflow-x:auto}summary{cursor:pointer;padding:8px 0}
        table{width:100%;border-collapse:collapse;font-size:13px;text-align:left}th,td{padding:11px 8px;border-bottom:1px solid #29384b}
        th{color:#a7b5c6;font-weight:500}td small{display:block;color:#8fa6bf;margin-top:4px}ul{line-height:1.7}.history{margin-top:32px;overflow-x:auto}
        footer{margin-top:28px;color:#8fa6bf;font-size:12px}footer code{overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere}.definitions{position:absolute}
        @media(max-width:850px){header,main{padding:20px}.maps{grid-template-columns:1fr}.metrics{grid-template-columns:repeat(2,1fr)}}
        """;
}
