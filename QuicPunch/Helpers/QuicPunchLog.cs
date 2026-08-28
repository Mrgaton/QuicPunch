using System;
using System.Diagnostics;

namespace QuicPunch
{
    public static class QuicPunchLog
    {
        public static bool EnableLogging { get; set; } = true;
        public static bool EnableErrorLogging { get; set; } = true;

        public static Action<string>? LogHandler { get; set; }
        public static Action<string>? ErrorHandler { get; set; }

        public static void Info(string message)
        {
            if (EnableLogging)
            {
                string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                if (LogHandler != null)
                    LogHandler(formatted);
                else
                    Console.WriteLine(formatted);
            }
        }

        public static void Error(string message, Exception? ex = null)
        {
            if (EnableErrorLogging || EnableLogging)
            {
                string text = ex != null ? $"{message}: {ex.GetType().Name}: {ex.Message}" : message;
                string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] {text}";
                if (ErrorHandler != null)
                    ErrorHandler(formatted);
                else if (LogHandler != null)
                    LogHandler(formatted);
                else
                    Console.Error.WriteLine(formatted);
            }
        }
    }
}
