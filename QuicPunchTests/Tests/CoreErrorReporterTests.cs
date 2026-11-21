using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using QuicNet.Diagnostics;
using QuicNet.WebUi;

namespace QuicPunchTests.Tests;

internal static class CoreErrorReporterTests
{
    internal static async Task RunAsync()
    {
        await CheckLegacyEndpointAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new CoreEndpoint();
        using var http = new HttpClient(handler);
        var reporter = new CoreErrorReporter(http, new Uri("https://test.invalid/logs/post"), "test-auth", "test-category", "windows");
        reporter.Enqueue(CoreErrorReporter.Describe("Prueba", CaptureError()));
        var web = new WebClientDiagnostics(reporter.Enqueue);
        string frames = string.Concat(Enumerable.Repeat("    at render (http://localhost/app.js?query-private:10:2)\n", 45));
        Require(web.Accept(JsonSerializer.SerializeToElement(new
        {
            level = "error",
            path = "/chat.html",
            context = "Enviar mensaje",
            message = "Falló el envío @everyone test-auth password=\"quoted private value\"",
            stack = "Error: Falló el envío\nAuthorization: Bearer header-private\n" + frames + "    at lastFrame (http://localhost/app.js:99:5)"
        })), "Web diagnostic rejected");
        var worker = reporter.RunAsync(timeout.Token);
        try
        {
            var (first, second) = await handler.Delivered.Task.WaitAsync(timeout.Token);
            Require(first == second, "Rate limiting must retry the same batch");
            Require(second.Contains("Prueba: InvalidOperationException") && second.Contains(nameof(CaptureError)),
                "Missing exception type or code location");
            Require(!second.Contains("private-payload"), "Exception message escaped to remote logs");
            Require(!second.Contains("test-auth") && !second.Contains("test-category"), "Authentication must stay in the headers");
            Require(second.Contains("[WEB ERROR] [/chat.html] Enviar mensaje: Falló el envío"),
                "Remote web log lost its page, context or message");
            Require(second.Contains("render (http://localhost/app.js:10:2)") && second.Contains("lastFrame (http://localhost/app.js:99:5)"),
                "Remote web stack was truncated or lost its source coordinates");
            Require(!second.Contains("query-private") && !second.Contains("header-private") &&
                !second.Contains("quoted private value") && !second.Contains("@everyone"), "Web log leaked a secret or active mention");
        }
        finally
        {
            await timeout.CancelAsync();
            await worker;
        }
    }

    private static async Task CheckLegacyEndpointAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var handler = new CoreEndpoint(privateLogs: false);
        using var http = new HttpClient(handler);
        var reporter = new CoreErrorReporter(http, new Uri("https://test.invalid/logs/post"), "test-auth", "test-category", "windows");
        reporter.Enqueue("private web error");
        var worker = reporter.RunAsync(stop.Token);
        try
        {
            await handler.Probed.Task.WaitAsync(stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await worker;
        }
    }

    private static Exception CaptureError()
    {
        try { throw new InvalidOperationException("private-payload"); }
        catch (Exception ex) { return ex; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CoreEndpoint(bool privateLogs = true) : HttpMessageHandler
    {
        private string? _first;
        internal readonly TaskCompletionSource Probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<(string, string)> Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Head)
            {
                Require(!request.Headers.Contains("auth") && !request.Headers.Contains("c-auth"), "Capability probe leaked credentials");
                var probe = new HttpResponseMessage(HttpStatusCode.NoContent);
                if (privateLogs) probe.Headers.Add("X-QuicNet-Private-Logs", "1");
                Probed.TrySetResult();
                return probe;
            }
            Require(privateLogs, "A report was posted to an older CoreServer");
            Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/logs/post", "Incorrect CoreServer route");
            Require(request.Headers.GetValues("auth").Single() == "test-auth", "Missing auth");
            Require(request.Headers.GetValues("c-auth").Single() == "test-category", "Missing category auth");
            Require(request.Headers.GetValues("category").Single() == "quic-net", "Incorrect category");
            Require(request.Headers.GetValues("channel").Single() == "errores-windows", "Incorrect channel");
            Require(request.Content?.Headers.ContentType?.MediaType == "text/plain", "CoreServer expects plain text");
            string body = await request.Content!.ReadAsStringAsync(token);
            if (_first == null)
            {
                _first = body;
                var rejected = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return rejected;
            }

            Delivered.TrySetResult((_first, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
