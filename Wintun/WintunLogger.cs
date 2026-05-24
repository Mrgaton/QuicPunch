using System;

namespace Wintun
{
    public static class WintunLogger
    {
        // Prevent GC from collecting the callback delegate
        private static WintunLoggerCallback? _currentLogger;

        /// <summary>
        /// Sets a global logger callback for Wintun.
        /// </summary>
        /// <param name="loggerAction">Action to receive log level and message. Pass null to disable logging.</param>
        public static void SetLogger(Action<WintunLoggerLevel, string>? loggerAction)
        {
            if (loggerAction == null)
            {
                _currentLogger = null;
                WintunApi.WintunSetLogger(null);
                return;
            }

            _currentLogger = (level, timestamp, message) =>
            {
                loggerAction(level, message);
            };

            WintunApi.WintunSetLogger(_currentLogger);
        }
    }
}
