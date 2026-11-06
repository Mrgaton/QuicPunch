using System.Diagnostics;
using System.Text.RegularExpressions;

namespace QuicPunchTests;

internal static class TestOutput
{
    private const int MaxFailureMessages = 3;

    public static async Task<int> RunAsync(string name, Func<Task> test, bool verbose, string? logDirectory = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var captured = new StringWriter();
        TextWriter synchronized = TextWriter.Synchronized(captured);
        var timer = Stopwatch.StartNew();
        Exception? exception = null;

        try
        {
            Console.SetOut(synchronized);
            Console.SetError(synchronized);
            await test();
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            timer.Stop();
        }

        string log = captured.ToString();
        string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        bool failed = exception != null || lines.Any(IsFailureLine);
        string duration = $"{timer.Elapsed.TotalSeconds:F1}s";
        string[] skips = lines.Where(IsSkipLine).Distinct().ToArray();
        bool onlySkipped = skips.Length > 0 && !lines.Any(line => !IsSkipLine(line) &&
            Regex.IsMatch(line, @"\b(PASS|PASSED|SUCCESS|OK)\b", RegexOptions.IgnoreCase));
        TextWriter output = failed ? originalError : originalOut;

        if (verbose)
        {
            output.Write(log);
            if (log.Length > 0 && !log.EndsWith('\n'))
                output.WriteLine();
            if (exception != null)
                output.WriteLine(exception);
        }

        string skipped = skips.Length > 0 ? $" skipped={skips.Length}" : "";
        output.WriteLine($"{(failed ? "FAIL" : onlySkipped ? "SKIP" : "OK")} {name} ({duration}){skipped}");
        if (failed && !verbose)
        {
            WriteFailureLog(output, lines);
            if (exception != null)
                output.WriteLine(Shorten($"{exception.GetType().Name}: {exception.Message}"));
        }

        // Keep the original diagnostics available without requiring another (possibly flaky) run.
        if (failed || skips.Length > 0)
            SaveLog(output, name, log, exception, logDirectory);
        return failed ? 1 : 0;
    }

    private static bool IsSkipLine(string line) =>
        Regex.IsMatch(line, @"\[(SKIP|SKIPPED)\]|\bSKIPPED\b", RegexOptions.IgnoreCase);

    private static bool IsFailureLine(string line)
    {
        string value = line.Trim();
        if (value.StartsWith("RESULTS:", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(value, @"\b[1-9]\d*\s+FAILED\b", RegexOptions.IgnoreCase);

        // Some native checks use this label for unsupported optional capabilities.
        value = value.Replace("SKIPPED/FAILED", "SKIPPED", StringComparison.OrdinalIgnoreCase);

        return value.StartsWith("[FAIL]", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[FAILED]", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[TEST ERROR]", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[UNOBSERVED TASK EXCEPTION]", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("[FATAL UNHANDLED EXCEPTION]", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(value, @"\bFAILED\b")
            || (value.StartsWith("[TEST", StringComparison.OrdinalIgnoreCase)
                && value.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteFailureLog(TextWriter output, string[] lines)
    {
        if (lines.Length == 0)
            return;

        string? lastCheck = lines.LastOrDefault(line =>
            line.TrimStart().StartsWith("[TEST", StringComparison.OrdinalIgnoreCase)
            && !IsFailureLine(line));
        if (lastCheck != null)
        {
            string label = lastCheck.Trim();
            int end = label.IndexOf("...", StringComparison.Ordinal);
            if (end >= 0)
                label = label[..(end + 3)];
            output.WriteLine($"Last check: {Shorten(label)}");
        }

        string[] failures = lines.Where(IsFailureLine).Distinct().ToArray();
        foreach (string failure in failures.Take(MaxFailureMessages))
            output.WriteLine(Shorten(failure.Trim()));
        if (failures.Length > MaxFailureMessages)
            output.WriteLine($"... {failures.Length - MaxFailureMessages} more failure messages in log");
    }

    private static void SaveLog(TextWriter output, string name, string log, Exception? exception, string? directory)
    {
        try
        {
            directory ??= Path.Combine(".artifacts", "test-output");
            Directory.CreateDirectory(directory);
            string safeName = Regex.Replace(name.TrimStart('-'), @"[^A-Za-z0-9_-]", "_");
            string path = Path.Combine(directory, $"{safeName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            File.WriteAllText(path, exception == null ? log : log + Environment.NewLine + exception);
            output.WriteLine($"Log: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine(Shorten($"Log unavailable: {ex.Message}. Use --verbose for details."));
        }
    }

    private static string Shorten(string line)
    {
        line = line.Replace('\r', ' ').Replace('\n', ' ');
        return line.Length <= 240 ? line : line[..237] + "...";
    }
}
