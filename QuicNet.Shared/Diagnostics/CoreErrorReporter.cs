using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QuicNet.Updates;
using QuicPunch;

namespace QuicNet.Diagnostics;

internal sealed class CoreErrorReporter
{
    private static readonly Uri DefaultEndpoint = new("https://gato.ovh/logs/post");
    private static readonly object StartupLock = new();
    private static CoreErrorReporter? _current;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _auth, _categoryKey, _platform;
    private volatile bool _privateEndpointConfirmed;
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    internal CoreErrorReporter(HttpClient http, Uri endpoint, string auth, string categoryKey, string platform)
    {
        _http = http;
        _endpoint = endpoint;
        _auth = auth;
        _categoryKey = categoryKey;
        _platform = platform;
    }

    internal static void Start(string dataDirectory)
    {
        lock (StartupLock)
        {
            if (_current != null) return;
            try
            {
                string path = Path.Combine(dataDirectory, "core-logs.json");
                if (!File.Exists(path)) return;
                using var stream = File.OpenRead(path);
                if (stream.Length > 8192) throw new InvalidDataException();
                using var config = JsonDocument.Parse(stream);
                var root = config.RootElement;
                string? auth = root.GetProperty("auth").GetString();
                string? categoryKey = root.GetProperty("categoryKey").GetString();
                Uri endpoint = DefaultEndpoint;
                if (root.TryGetProperty("endpoint", out var url))
                    endpoint = new Uri(url.GetString()!, UriKind.Absolute);
                if (!ValidHeader(auth) || !ValidHeader(categoryKey) || endpoint.Scheme != Uri.UriSchemeHttps ||
                    endpoint.AbsolutePath != "/logs/post" || endpoint.UserInfo.Length != 0 ||
                    endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                    throw new InvalidDataException();

                var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                var reporter = new CoreErrorReporter(http, endpoint, auth!, categoryKey!,
                    OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsWindows() ? "windows" : "linux");
                Volatile.Write(ref _current, reporter);
                QuicPunchLog.ErrorReported += ex => Report("Transporte", ex);
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    if (!args.IsTerminating) { Report("Excepción no controlada", args.ExceptionObject as Exception); return; }
                    reporter.TrySendFatal(args.ExceptionObject as Exception);
                };
                TaskScheduler.UnobservedTaskException += (_, args) => Report("Tarea no observada", args.Exception);
                _ = Task.Run(() => reporter.RunAsync(CancellationToken.None));
            }
            catch (Exception)
            {
                Console.Error.WriteLine("[QuicNet] No se pudo cargar la configuración de registros remotos.");
            }
        }
    }

    private static bool ValidHeader(string? value) =>
        value is { Length: > 0 and <= 512 } && value.All(c => c is >= '!' and <= '~');

    internal static void Report(string area, Exception? exception = null)
    {
        var reporter = Volatile.Read(ref _current);
        if (reporter == null) return;
        try { reporter.Enqueue(Describe(area, exception)); }
        catch (Exception) { /* Diagnostics must not interrupt the application. */ }
    }

    internal static void ReportWebError(string diagnostic) =>
        Volatile.Read(ref _current)?.Enqueue(diagnostic);

    internal static string Describe(string area, Exception? exception)
    {
        if (exception == null) return area;
        Exception cause = exception.GetBaseException();
        var frames = new StackTrace(cause, false).ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var frame = frames.FirstOrDefault(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        return frame == null ? $"{area}: {cause.GetType().Name}" : $"{area}: {cause.GetType().Name}\n{frame.Trim()}";
    }

    internal void Enqueue(string diagnostic) =>
        _pending.Writer.TryWrite($"[{DateTimeOffset.UtcNow:O}] {diagnostic}");

    internal async Task RunAsync(CancellationToken token)
    {
        bool deliveryFailed = false;
        try
        {
            while (await _pending.Reader.WaitToReadAsync(token))
            {
                var batch = new StringBuilder();
                for (int i = 0; i < 4 && _pending.Reader.TryRead(out string? entry); i++)
                    batch.AppendLine(entry);
                bool accepted = await DeliverAsync(batch.ToString(), token);
                if (!accepted && !deliveryFailed)
                    Console.Error.WriteLine("[QuicNet] CoreServer no aceptó los registros remotos.");
                deliveryFailed = !accepted;
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<bool> DeliverAsync(string batch, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            TimeSpan delay = TimeSpan.FromSeconds(3 * (attempt + 1));
            try
            {
                using var response = await SendAsync(batch, token);
                if (response == null) return false;
                if (response.IsSuccessStatusCode) return true;
                int status = (int)response.StatusCode;
                if (status is not (408 or 429) && (status < 500 || status == 505)) return false;
                if (response.Headers.RetryAfter?.Delta is { } retryAfter)
                    delay = retryAfter;
                else if (response.Headers.RetryAfter?.Date is { } retryDate)
                    delay = retryDate - DateTimeOffset.UtcNow;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 60)), token);
        }
        return false;
    }

    private async Task<HttpResponseMessage?> SendAsync(string batch, CancellationToken token)
    {
        if (!_privateEndpointConfirmed)
        {
            using var probe = new HttpRequestMessage(HttpMethod.Head, _endpoint);
            using var capability = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!capability.IsSuccessStatusCode ||
                !capability.Headers.TryGetValues("X-QuicNet-Private-Logs", out var values) || !values.Contains("1"))
                return null;
            _privateEndpointConfirmed = true;
        }

        batch = batch.Replace(_auth, "[redacted]", StringComparison.Ordinal)
            .Replace(_categoryKey, "[redacted]", StringComparison.Ordinal)
            .Replace("@everyone", "@\u200beveryone", StringComparison.OrdinalIgnoreCase)
            .Replace("@here", "@\u200bhere", StringComparison.OrdinalIgnoreCase)
            .Replace("<@", "<@\u200b", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(batch, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("auth", _auth);
        request.Headers.Add("c-auth", _categoryKey);
        request.Headers.Add("category", "quic-net");
        request.Headers.Add("channel", $"errores-{_platform}");
        request.Headers.Add("title", $"QuicNet {ReleaseIdentity.Version} ({ReleaseIdentity.Build})");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private void TrySendFatal(Exception? exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = SendAsync($"[{DateTimeOffset.UtcNow:O}] {Describe("Error fatal", exception)}", timeout.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception) { /* The process is already terminating. */ }
    }
}
