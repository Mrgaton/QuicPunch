using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuicNet.WebUi;

// One instance per authenticated socket. Client diagnostics never execute commands.
internal sealed class WebClientDiagnostics
{
    private long _window = Environment.TickCount64;
    private int _count;
    internal bool Accept(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        string? Read(string key, int limit) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && text.Length <= limit ? text : null;
        string? level = Read("level", 8), message = Read("message", 2048);
        if (level is not ("warn" or "error") || string.IsNullOrWhiteSpace(message)) return false;
        if (Environment.TickCount64 - _window >= 1000) { _window = Environment.TickCount64; _count = 0; }
        if (++_count > 20) return false;
        string Clean(string text) => Regex.Replace(Regex.Replace(text, @"(https?://[^\s?]+)\?[^\s)]+", "$1"),
            @"\b(csrf|token|password|authorization|secret)(\s*[:=]\s*)[^\s,;&]+", "$1$2[redacted]", RegexOptions.IgnoreCase)
            .Replace("\u001b", "").Replace("\r", "");
        string Inline(string text) => Clean(text).Replace('\n', ' ');
        string entry = $"[WEB {level.ToUpperInvariant()}] [{Inline(Read("path", 256) ?? "/")}] {Inline(Read("context", 128) ?? "JavaScript")}: {Inline(message)}";
        if (Read("stack", 4096) is { Length: > 0 } stack) entry += "\n  > " + Clean(stack).Replace("\n", "\n  > ");
        Console.Error.WriteLine(entry);
        WebUiServer.LogEvent(entry);
        return true;
    }
}
