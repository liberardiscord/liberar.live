using System;
using System.IO;

namespace Droute.UpdaterHook
{
    public static class Logger
    {
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updaterHook.log");
        private static readonly object _lock = new object();

        static Logger()
        {
            try
            {
                lock (_lock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    File.AppendAllText(_logPath, $"// updaterHook session started at {timestamp}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Trace(string message)
            => Log("TRACE", message);

        public static void Debug(string message)
            => Log("DEBUG", message);

        public static void Info(string message)
            => Log("INFO", message);

        public static void Warning(string message)
            => Log("WARN", message);

        public static void Error(string message)
            => Log("ERROR", message);

        private static void Log(string level, string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logLine = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                lock (_lock) File.AppendAllText(_logPath, logLine);
            }
            catch { }
        }
    }
}
