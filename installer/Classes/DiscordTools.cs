using Droute.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Droute.Installer.Classes
{
    internal static class DiscordTools
    {
        public static bool CloseWait(DiscordManager.Branches branch, int timeoutMs = 5000, int retryIntervalMs = 150)
        {
            if (timeoutMs < 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (retryIntervalMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(retryIntervalMs));

            var stopwatch = Stopwatch.StartNew();
            do
            {
                DiscordManager.Close(branch);
                if (!DiscordManager.IsDiscordRunning(branch))
                    return true;

                int remainingMs = timeoutMs - (int)stopwatch.ElapsedMilliseconds;
                if (remainingMs <= 0)
                    break;
                Thread.Sleep(Math.Min(retryIntervalMs, remainingMs));
            }
            while (stopwatch.ElapsedMilliseconds < timeoutMs);

            return !DiscordManager.IsDiscordRunning(branch);
        }

        public static bool RestartAndWait(IReadOnlyList<DiscordManager.Branches> branches,
                                          int closeTimeoutMs = 10000,
                                          int startupTimeoutMs = 45000)
        {
            if (branches == null)
                throw new ArgumentNullException(nameof(branches));
            if (branches.Count == 0)
                throw new ArgumentException("Nenhuma instala\u00e7\u00e3o do Discord foi selecionada.", nameof(branches));
            if (startupTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(startupTimeoutMs));

            var previousProcessIds = new Dictionary<DiscordManager.Branches, HashSet<int>>();
            foreach (DiscordManager.Branches branch in branches)
                previousProcessIds[branch] = CaptureProcessIds(branch);

            // A restart is only considered real after every old process is gone.
            foreach (DiscordManager.Branches branch in branches)
            {
                if (!CloseWait(branch, closeTimeoutMs))
                    throw new InvalidOperationException("N\u00e3o foi poss\u00edvel encerrar o Discord " + branch + ".");
            }

            foreach (DiscordManager.Branches branch in branches)
                DiscordManager.Launch(branch);

            var stopwatch = Stopwatch.StartNew();
            long readySinceMs = -1;
            while (stopwatch.ElapsedMilliseconds < startupTimeoutMs)
            {
                bool allReady = true;
                foreach (DiscordManager.Branches branch in branches)
                {
                    if (!HasNewResponsiveWindow(branch, previousProcessIds[branch]))
                    {
                        allReady = false;
                        break;
                    }
                }

                if (allReady)
                {
                    if (readySinceMs < 0)
                        readySinceMs = stopwatch.ElapsedMilliseconds;
                    else if (stopwatch.ElapsedMilliseconds - readySinceMs >= 1000)
                        return true;
                }
                else
                {
                    readySinceMs = -1;
                }

                Thread.Sleep(200);
            }

            return false;
        }

        private static HashSet<int> CaptureProcessIds(DiscordManager.Branches branch)
        {
            var result = new HashSet<int>();
            Process[] processes = Process.GetProcessesByName(DiscordManager.GetDiscordProcessName(branch));
            try
            {
                foreach (Process process in processes)
                    result.Add(process.Id);
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
            return result;
        }

        private static bool HasNewResponsiveWindow(DiscordManager.Branches branch, HashSet<int> previousProcessIds)
        {
            Process[] processes = Process.GetProcessesByName(DiscordManager.GetDiscordProcessName(branch));
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (!previousProcessIds.Contains(process.Id) &&
                            process.MainWindowHandle != IntPtr.Zero &&
                            process.Responding)
                        {
                            return true;
                        }
                    }
                    catch { }
                }
                return false;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }
    }
}
