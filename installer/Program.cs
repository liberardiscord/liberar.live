using Droute.Core;
using Droute.Installer.Classes;
using Droute.Installer.Forms;
using Droute.Installer.Properties;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Droute.Installer
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private static bool createdNew;
        private static Mutex mtx;

        /// <summary>
        /// Hands back the WebView2 assemblies, which travel inside this executable
        /// rather than beside it.
        ///
        /// They are not merged in with everything else because ILRepack internalizes
        /// what it merges, and WebView2 reaches its own types through COM interop,
        /// which that breaks. Resolving them here keeps the single file and keeps
        /// the control working. It runs before anything can touch those types, and
        /// a failure to load simply means the program has no advertising strip.
        /// </summary>
        private static Assembly ResolverWebView2(object sender, ResolveEventArgs args)
        {
            string nome = new AssemblyName(args.Name).Name;
            try
            {
                if (nome == "Microsoft.Web.WebView2.Core")
                    return Assembly.Load(Resources.WebView2Core);
                if (nome == "Microsoft.Web.WebView2.WinForms")
                    return Assembly.Load(Resources.WebView2WinForms);
            }
            catch
            {
                // Returning null lets the runtime report the miss the usual way,
                // which the advertising strip already treats as "not available".
            }
            return null;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolverWebView2;
            mtx = new Mutex(true, "discord.proxy.hardcoded", out createdNew);
            if (!createdNew)
                return;

            if (args.Length > 0)
            {
                AttachConsole(-1);
                RunCli(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SimpleMainForm());
        }

        private static void RunCli(string[] args)
        {
            bool install = args.Any(arg => string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(arg, "-install", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(arg, "-uninstall", StringComparison.OrdinalIgnoreCase));
            if (install == uninstall)
            {
                CliLogger.WriteError("Use --install ou --uninstall.");
                Environment.ExitCode = 2;
                return;
            }

            var branches = DiscordManager.GetInstalledBranches();
            if (branches.Count == 0)
            {
                CliLogger.WriteError("Nenhuma instalação do Discord foi encontrada.");
                Environment.ExitCode = 1;
                return;
            }

            using (var logger = new CliLogger())
            {
                try
                {
                    foreach (var branch in branches)
                    {
                        if (!DiscordTools.CloseWait(branch, 10000))
                            throw new InvalidOperationException("Não foi possível fechar o Discord " + branch + ".");
                    }

                    bool success = true;
                    foreach (var branch in branches)
                        success &= install ? PatchTools.Install(branch) : PatchTools.Remove(branch);
                    Environment.ExitCode = success ? 0 : 1;
                }
                catch (Exception ex)
                {
                    CliLogger.WriteError(ex.Message);
                    Environment.ExitCode = 1;
                }
            }
        }
    }
}
