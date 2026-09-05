using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch
{
    public static class QuicPunchLog
    {
        public static bool EnableLogging { get; set; } = true;
        public static bool EnableErrorLogging { get; set; } = true;
        public static bool EnableFileLogging { get; set; } = true;

        public static Action<string>? LogHandler { get; set; }
        public static Action<string>? ErrorHandler { get; set; }

        private static RotatingFileLogger? _fileLogger;
        private static readonly object _fileLoggerLock = new();

        public static RotatingFileLogger FileLogger
        {
            get
            {
                if (_fileLogger != null) return _fileLogger;
                lock (_fileLoggerLock)
                {
                    return _fileLogger ??= new RotatingFileLogger();
                }
            }
            set
            {
                lock (_fileLoggerLock)
                {
                    _fileLogger = value;
                }
            }
        }

        public static string LogDirectory
        {
            get => FileLogger.LogDirectory;
            set => FileLogger.LogDirectory = value;
        }

        static QuicPunchLog()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _fileLogger?.Dispose(); } catch { }
            };
        }

        public static void Info(string message)
        {
            if (EnableLogging)
            {
                string formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

                if (LogHandler != null)
                    LogHandler(formatted);
                else
                    Console.WriteLine(formatted);

                if (EnableFileLogging)
                {
                    FileLogger.Write(formatted);
                }
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

                if (EnableFileLogging)
                {
                    FileLogger.Write(formatted);
                }
            }
        }

        public static void Flush()
        {
            _fileLogger?.Flush();
        }

        public static Task FlushAsync()
        {
            return _fileLogger != null ? _fileLogger.FlushAsync() : Task.CompletedTask;
        }
    }
}
