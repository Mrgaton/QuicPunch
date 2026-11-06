namespace QuicPunchTests.Tests;

internal static class TestOutputTests
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qp-test-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var success = await CaptureAsync(() =>
            {
                Console.WriteLine("A verbose banner");
                Console.Error.WriteLine("[12:00:00] [ERROR] Expected error exercised by a logging test");
                Console.WriteLine("[PASS] expected failure handled");
                Console.WriteLine("RESULTS: 1 PASSED, 0 FAILED");
            }, directory);
            Check(success.Code == 0 && Lines(success.Out).Length == 1 && success.Error == "",
                "successful suites must produce just one result line");
            Check(Directory.GetFiles(directory).Length == 0, "successful suites need no diagnostic file");

            var failure = await CaptureAsync(() =>
            {
                Console.Write("[TEST] payload roundtrip... ");
                Console.WriteLine("FAILED (checksum mismatch)");
                Console.Error.WriteLine("[ERROR] additional context");
                Console.WriteLine("[PASS] a later check must not hide the failure");
            }, directory);
            Check(failure.Code == 1 && failure.Out == "" && failure.Error.Contains("checksum mismatch"),
                "printed failures must exit 1 even when later checks pass");
            string failureLog = ReadLog(failure.Error);
            Check(failureLog.Contains("additional context") && failureLog.Contains("a later check"),
                "the saved log must preserve both console streams");

            var summaryFailure = await CaptureAsync(() => Console.WriteLine("RESULTS: 5 PASSED, 2 FAILED"), directory);
            Check(summaryFailure.Code == 1, "a nonzero failure summary must not report success");

            var skipped = await CaptureAsync(() => Console.WriteLine("[SKIP] Bypass unsupported"), directory);
            Check(skipped.Code == 0 && skipped.Out.StartsWith("SKIP ") && skipped.Out.Contains("skipped=1"),
                "a skipped suite must be distinguishable from a successful suite");
            Check(ReadLog(skipped.Out).Contains("Bypass unsupported"), "skip reasons must remain available");

            var partial = await CaptureAsync(() =>
            {
                Console.WriteLine("[PASS] required check");
                Console.WriteLine("[TEST] optional native check... SKIPPED/FAILED");
                Console.WriteLine("[SKIP] other unavailable capability");
            }, directory);
            Check(partial.Code == 0 && partial.Out.StartsWith("OK ") && partial.Out.Contains("skipped=2"),
                "optional native skips must be counted without becoming failures");
            Check(Lines(partial.Out).Length == 2, "skip details must stay in the log");

            var exception = await CaptureAsync(() =>
                throw new InvalidOperationException("first line\n" + new string('x', 2000)), directory);
            Check(exception.Code == 1 && exception.Error.Contains("InvalidOperationException: first line"),
                "exceptions must retain their type and exit code");
            Check(!exception.Error.Contains(new string('x', 300)) && !exception.Error.Contains("   at "),
                "compact errors must not dump multiline messages or stack traces");
            Check(ReadLog(exception.Error).Contains(new string('x', 2000)), "full exceptions must survive in the log");

            var manyFailures = await CaptureAsync(() =>
            {
                for (int i = 0; i < 100; i++) Console.WriteLine($"[FAIL] case {i}");
            }, directory);
            Check(manyFailures.Code == 1 && Lines(manyFailures.Error).Length <= 6,
                "large failure sets must have bounded console output");
            Check(ReadLog(manyFailures.Error).Contains("[FAIL] case 99"), "bounded output must not lose diagnostics");

            var verbose = await CaptureAsync(() =>
            {
                Console.WriteLine("full stdout");
                Console.Error.WriteLine("full stderr");
            }, directory, verbose: true);
            Check(verbose.Code == 0 && verbose.Out.Contains("full stdout") && verbose.Out.Contains("full stderr"),
                "verbose mode must show the complete captured output");

            string notDirectory = Path.Combine(directory, "file");
            File.WriteAllText(notDirectory, "occupied");
            var unavailableLog = await CaptureAsync(() => Console.WriteLine("[FAIL] original cause"), notDirectory);
            Check(unavailableLog.Code == 1 && unavailableLog.Error.Contains("original cause")
                && unavailableLog.Error.Contains("Log unavailable:"), "log write errors must preserve the test result");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("[PASS] compact output, failure exit codes, skips, complete logs and verbose mode");
    }

    private static async Task<(int Code, string Out, string Error)> CaptureAsync(Action test, string directory, bool verbose = false)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            TextWriter callerOut = Console.Out;
            TextWriter callerError = Console.Error;
            int code = await TestOutput.RunAsync("--fixture", () => { test(); return Task.CompletedTask; }, verbose, directory);
            Check(ReferenceEquals(Console.Out, callerOut) && ReferenceEquals(Console.Error, callerError),
                "nested runs must restore their caller's console");
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string ReadLog(string output) =>
        File.ReadAllText(Lines(output).Single(line => line.StartsWith("Log: "))[5..]);

    private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
