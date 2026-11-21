using System.Text.Json;
using System.Text.RegularExpressions;
using QuicNet.Diagnostics;

namespace QuicNet.WebUi;

// One instance per authenticated socket. Client diagnostics never execute commands.
internal sealed class WebClientDiagnostics(Action<string>? reportError = null)
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
        string Clean(string text)
        {
            text = Regex.Replace(text, """((?:(?:https?|wss?)://|/)[^\s?#)"'<>]*)[?#][^\s)"'<>]*?(?=:\d+:\d+(?:[\s)]|$)|[\s)"'<>]|$)""",
                "$1", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b((?:authorization|proxy-authorization|cookie|set-cookie)[ \t]*:)[^\r\n]*",
                "$1 [redacted]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, """(\b[\w-]*(?:csrf|token|password|authorization|secret|api[-_]?key|cookie)[\w-]*["']?[ \t]*[:=][ \t]*)(?:"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*'|(?:bearer|basic)[ \t]+[^\s,;&]+|[^\s,;&]+)""",
                "$1[redacted]", RegexOptions.IgnoreCase);
            return text.Replace("\u001b", "").Replace("\r", "");
        }
        string Inline(string text) => Clean(text).Replace('\n', ' ');
        string entry = $"[WEB {level.ToUpperInvariant()}] [{Inline(Read("path", 256) ?? "/")}] {Inline(Read("context", 128) ?? "JavaScript")}: {Inline(message)}";
        if (Read("stack", 4096) is { Length: > 0 } stack) entry += "\n  > " + Clean(stack).Replace("\n", "\n  > ");
        Console.Error.WriteLine(entry);
        WebUiServer.LogEvent(entry);
        if (level == "error")
            (reportError ?? CoreErrorReporter.ReportWebError)(entry);
        return true;
    }
}
