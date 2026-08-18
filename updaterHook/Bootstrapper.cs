using Droute.Core;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using CoreDroute = Droute.Core.Droute;

namespace Droute.UpdaterHook
{
    public class Bootstrapper : AppDomainManager
    {
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                Logger.Debug("domain initialized, subscribing to assembly load events");
            }
            catch (Exception ex)
            {
                Logger.Error($"failed to initialize domain manager: {ex.Message}");
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (args?.LoadedAssembly == null) return;

                string name = args.LoadedAssembly.GetName()?.Name;
                if (string.IsNullOrEmpty(name)) return;

                if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    Logger.Info("Update.exe loaded, preparing patches");

                    Type type = args.LoadedAssembly.GetType("Squirrel.Update.Program");
                    if (type == null)
                    {
                        Logger.Error("type \"Squirrel.Update.Program\" not found in assembly");
                        return;
                    }

                    MethodInfo baseMethod = type.GetMethod("ProcessStart",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (baseMethod == null)
                    {
                        Logger.Error("target method \"ProcessStart\" not found");
                        return;
                    }

                    MethodInfo prefixMethod = typeof(Bootstrapper).GetMethod(nameof(MyProcessStart),
                        BindingFlags.Static | BindingFlags.Public);
                    if (prefixMethod == null)
                    {
                        Logger.Error("detour method \"MyProcessStart]\" not found in hook assembly");
                        return;
                    }

                    Logger.Debug("applying harmony prefix patch for ProcessStart");

                    var harmony = new Harmony("Droute.UpdaterHook");
                    harmony.Patch(baseMethod, prefix: new HarmonyMethod(prefixMethod));

                    Logger.Info("hooks successfully installed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"exception during assembly load interception: {ex.Message}");
            }
        }

        public static bool MyProcessStart(object __instance, string exeName, string arguments, bool shouldWait)
        {
            Logger.Trace($"ProcessStart triggered: exeName=\"{exeName}\", args=\"{arguments}\", wait={shouldWait}");

            string branchRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!CoreDroute.IsTargetExecutable(branchRoot, exeName))
            {
                Logger.Debug($"skipping non-Discord process: {exeName}");
                return true;
            }

            try
            {
                if (string.IsNullOrEmpty(branchRoot))
                {
                    Logger.Error("AppContext.BaseDirectory returned null or empty");
                    return true;
                }

                Logger.Debug($"resolving last version path from: {branchRoot}");
                string appDirectory = DiscordManager.GetLastVersionPath(branchRoot);

                if (string.IsNullOrEmpty(appDirectory) || !Directory.Exists(appDirectory))
                {
                    Logger.Error($"resolved app directory is invalid or missing: {appDirectory}");
                    return true;
                }

                Logger.Info($"target app directory: {appDirectory}");

                if (CoreDroute.IsInstalled(appDirectory))
                {
                    Logger.Info("patch already been applied, skip installation.");
                    return true;
                }

                Logger.Info("installing patch...");
                CoreDroute.Install(appDirectory, Properties.Resources.Droute64);

                Logger.Debug("patching completed successfully!");
            }
            catch (Exception ex)
            {
                Logger.Error($"unexpected exception in MyProcessStart: {ex.Message}");
                Logger.Trace($"stack trace: {ex.StackTrace}");
            }

            return true;
        }
    }
}
