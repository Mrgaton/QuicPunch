using System;

namespace QuicPunch.Vpn.Windows
{
    public static class WintunLogger
    {
        private static WintunLoggerCallback? _loggerCallback;

        public static void SetLogger(Action<WintunLoggerLevel, ulong, string>? logger)
        {
            if (logger == null)
            {
                _loggerCallback = null;
                WintunApi.WintunSetLogger(null);
                return;
            }

            _loggerCallback = (level, timestamp, message) => logger(level, timestamp, message);
            WintunApi.WintunSetLogger(_loggerCallback);
        }
    }
}
