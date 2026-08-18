using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Droute.Core
{
    public static class DiscordManager
    {
        public enum Branches
        {
            Stable,
            Canary,
            PTB
        }

        public static List<Branches> GetInstalledBranches()
        {
            List<Branches> result = new List<Branches>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Discord Stable
            if (Directory.Exists(Path.Combine(localAppData, "Discord")))
                result.Add(Branches.Stable);

            // Discord Canary
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordCanary")))
                result.Add(Branches.Canary);

            // Discord Public Test Build (PTB)
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordPTB")))
                result.Add(Branches.PTB);

            return result;
        }

        public static string GetBranchRoot(Branches branch)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            switch (branch)
            {
                case Branches.Stable:
                    return Path.Combine(localAppData, "Discord");
                case Branches.Canary:
                    return Path.Combine(localAppData, "DiscordCanary");
                case Branches.PTB:
                    return Path.Combine(localAppData, "DiscordPTB");
                default:
                    throw new ArgumentException("Invalid branch!");
            }
        }

        public static string GetLastVersionPath(string branchRoot)
        {
            if (string.IsNullOrEmpty(branchRoot) || !Directory.Exists(branchRoot))
                throw new ArgumentException("Branch root path is invalid!");

            var appDirectory = Directory.GetDirectories(branchRoot, "app-*")
                .Select(dirPath => new
                {
                    Path = dirPath,
                    Name = Path.GetFileName(dirPath).Replace("app-", "")
                })
                .Where(x => Version.TryParse(x.Name, out _))
                .Select(x => new
                {
                    x.Path,
                    Version = Version.Parse(x.Name)
                })
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();

            if (appDirectory == null)
                throw new DirectoryNotFoundException("No valid version directories found in the branch root!");

            return appDirectory.Path;
        }

        public static string GetDiscordExecutablePath(string branchRoot, Branches branch = Branches.Stable)
        {
            string lastVersionPath = GetLastVersionPath(branchRoot);
            switch (branch)
            {
                case Branches.Stable:
                    return Path.Combine(lastVersionPath, "Discord.exe");
                case Branches.Canary:
                    return Path.Combine(lastVersionPath, "DiscordCanary.exe");
                case Branches.PTB:
                    return Path.Combine(lastVersionPath, "DiscordPTB.exe");
                default:
                    throw new ArgumentException("Invalid branch!");
            }
        }

        public static string GetDiscordProcessName(Branches branch)
        {
            switch (branch)
            {
                case Branches.Stable:
                    return "Discord";
                case Branches.Canary:
                    return "DiscordCanary";
                case Branches.PTB:
                    return "DiscordPTB";
                default:
                    throw new ArgumentException("Invalid branch!");
            }
        }

        public static bool IsDiscordRunning(Branches branch)
        {
            string processName = GetDiscordProcessName(branch);
            var processes = Process.GetProcessesByName(processName);

            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        public static void Close(Branches branch)
        {
            string processName = GetDiscordProcessName(branch);
            var processes = Process.GetProcessesByName(processName);

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"error while closing {processName}: {ex}");
                    }
                }
            }
        }

        public static void Launch(Branches branch)
        {
            string branchRoot = GetBranchRoot(branch);
            string executablePath = GetDiscordExecutablePath(branchRoot, branch);

            if (!File.Exists(executablePath))
                throw new FileNotFoundException("Discord executable not found!", executablePath);

            Process.Start(executablePath);
        }
    }
}
