using System;
using System.IO;

namespace Droute.Installer.Classes
{
    internal sealed class CliLogger : IDisposable
    {
        private const int LevelWidth = 5;
        private static readonly object ConsoleSync = new object();
        private bool _disposed;

        public CliLogger()
        {
            PatchTools.OnLog += OnLog;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            PatchTools.OnLog -= OnLog;
            _disposed = true;
        }

        public static void WriteOk(string message)
            => Write("OK", ConsoleColor.Green, message, Console.Out);

        public static void WriteError(string message)
            => Write("ERROR", ConsoleColor.Red, message, Console.Error);

        private static void OnLog(string content)
        {
            if (content == null)
                return;

            if (TryStripPrefix(content, "[ STATUS ]", out string message))
                Write("INFO", ConsoleColor.Blue, message, Console.Out);
            else if (TryStripPrefix(content, "[ INFO ]", out message))
                Write("INFO", ConsoleColor.Blue, message, Console.Out);
            else if (TryStripPrefix(content, "[ STAGE ]", out message))
                Write("STAGE", ConsoleColor.Cyan, message, Console.Out);
            else if (TryStripPrefix(content, "[ OK ]", out message))
                Write("OK", ConsoleColor.Green, message, Console.Out);
            else if (TryStripPrefix(content, "[ WARN ]", out message))
                Write("WARN", ConsoleColor.Yellow, message, Console.Out);
            else if (TryStripPrefix(content, "[ FATAL ]", out message))
                Write("ERROR", ConsoleColor.Red, message.TrimStart(':', ' '), Console.Error);
            else if (TryStripPrefix(content, "[ > ]", out message) ||
                     TryStripPrefix(content, "[ < ]", out message))
                Write("FILE", ConsoleColor.Magenta, message, Console.Out);
            else
                Write("LOG", ConsoleColor.Gray, content, Console.Out);
        }

        private static bool TryStripPrefix(string content, string prefix, out string message)
        {
            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                message = null;
                return false;
            }

            message = content.Substring(prefix.Length).TrimStart();
            return true;
        }

        private static void Write(string level, ConsoleColor color, string message, TextWriter writer)
        {
            lock (ConsoleSync)
            {
                string paddedLevel = level.PadRight(LevelWidth);
                bool redirected = ReferenceEquals(writer, Console.Error)
                    ? Console.IsErrorRedirected
                    : Console.IsOutputRedirected;

                if (redirected)
                {
                    writer.WriteLine($"[{paddedLevel}] {message}");
                    return;
                }

                ConsoleColor previousForeground = Console.ForegroundColor;
                ConsoleColor previousBackground = Console.BackgroundColor;

                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    writer.Write($"[{DateTime.Now:HH:mm:ss}] ");

                    Console.BackgroundColor = color;
                    Console.ForegroundColor = ConsoleColor.Black;
                    writer.Write($" {paddedLevel} ");

                    Console.BackgroundColor = previousBackground;
                    Console.ForegroundColor = color;
                    writer.Write(" ");
                    writer.WriteLine(message);
                }
                finally
                {
                    Console.ForegroundColor = previousForeground;
                    Console.BackgroundColor = previousBackground;
                }
            }
        }
    }
}
