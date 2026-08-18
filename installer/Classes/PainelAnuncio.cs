using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Droute.Installer.Properties;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Droute.Installer.Classes
{
    /// <summary>
    /// Where the advertising address comes from, resolved the same way the broker
    /// address is: environment first, then a file next to the executable, then the
    /// compiled default. Nothing here is a secret and overriding it grants nothing.
    /// </summary>
    internal static class PainelConfig
    {
        // Public builds do not contact a website by default. Operators may opt in
        // with LIBERAR_PAINEL_URL or painel.url next to the executable.
        private const string UrlPadrao = "";

        public static readonly string Url = Resolver();

        private static string Resolver()
        {
            string configurada = Environment.GetEnvironmentVariable("LIBERAR_PAINEL_URL");

            if (string.IsNullOrWhiteSpace(configurada))
            {
                try
                {
                    string pasta = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(pasta))
                    {
                        string caminho = Path.Combine(pasta, "painel.url");
                        if (File.Exists(caminho))
                            configurada = File.ReadAllText(caminho);
                    }
                }
                catch
                {
                    // An unreadable override falls back to the default. The panel is
                    // never allowed to be a reason the program fails to start.
                }
            }

            return string.IsNullOrWhiteSpace(configurada) ? UrlPadrao : configurada.Trim();
        }
    }

    /// <summary>
    /// The advertising strip at the bottom of the window.
    ///
    /// It hosts a page of ours, which is what carries the ad tags, so the request
    /// reaches the network as ordinary desktop web traffic from our own domain.
    /// Three rules are wired in and none of them are negotiable: the strip is
    /// labelled as advertising, nothing in the program's actual flow ever waits on
    /// it, and if it cannot load it removes itself instead of leaving a hole.
    ///
    /// Anything the page tries to open, in a new window or by navigating away,
    /// leaves for the default browser. A click has to be able to land somewhere or
    /// the placement is worthless, and it must never land on top of the interface.
    /// </summary>
    internal sealed class PainelAnuncio : Panel
    {
        public const int LarguraAnuncio = 300;
        public const int AlturaAnuncio = 250;
        public const int AlturaTotal = 12 + 12 + 8 + AlturaAnuncio + 14;

        /// <summary>Raised once, on the UI thread, when the strip cannot be shown.</summary>
        public event Action Indisponivel;

        /// <summary>
        /// Raised once, on the UI thread, when the page confirms it actually has
        /// something to show. The window reserves no room for the strip until this
        /// fires, so a machine that cannot load it never sees the space appear.
        /// </summary>
        public event Action Disponivel;

        private readonly Label rotulo;
        private readonly WebView2 navegador;
        private readonly Uri origem;
        private Timer paciencia;
        private bool desistiu;
        private bool pronto;
        private bool confirmado;

        public PainelAnuncio()
        {
            DoubleBuffered = true;

            rotulo = new Label
            {
                AutoSize = false,
                Font = Tema.Fonte(7.5f),
                Text = "publicidade",
                TextAlign = ContentAlignment.MiddleLeft
            };

            navegador = new WebView2 { DefaultBackgroundColor = Color.Transparent };
            navegador.Size = new Size(LarguraAnuncio, AlturaAnuncio);

            Controls.Add(rotulo);
            Controls.Add(navegador);
            Height = AlturaTotal;
            // Nothing is reserved for advertising until the page says it filled the
            // slot. Starting visible meant the window opened tall and collapsed a
            // fraction of a second later, on every machine where the strip does not
            // load, which is a resize the user did not ask for and cannot explain.
            Visible = false;
            Uri.TryCreate(PainelConfig.Url, UriKind.Absolute, out origem);
            AplicarTema();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (rotulo == null || navegador == null)
                return;
            rotulo.SetBounds(20, 12, 200, 13);
            navegador.SetBounds((Width - LarguraAnuncio) / 2, 12 + 12 + 8, LarguraAnuncio, AlturaAnuncio);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var caneta = new Pen(Tema.Linha))
                e.Graphics.DrawLine(caneta, 0, 0, Width, 0);
        }

        public void AplicarTema()
        {
            BackColor = Tema.Fundo;
            rotulo.BackColor = Tema.Fundo;
            rotulo.ForeColor = Tema.Fantasma;
            if (pronto)
            {
                try { navegador.CoreWebView2.PostWebMessageAsString("tema:" + (Tema.Escuro ? "escuro" : "claro")); }
                catch { }
            }
            Invalidate();
        }

        /// <summary>
        /// Brings the strip up in the background. Every failure path ends in
        /// <see cref="Indisponivel"/>, which is the caller's cue to drop the strip
        /// and shrink the window, so a machine without the WebView2 runtime simply
        /// gets a shorter program instead of an error.
        /// </summary>
        public async void Iniciar()
        {
            if (desistiu || origem == null)
            {
                Desistir();
                return;
            }

            try
            {
                if (!CarregadorNativo.Preparar())
                {
                    Desistir();
                    return;
                }

                string dadosDoUsuario = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "liberar.live", "painel");
                Directory.CreateDirectory(dadosDoUsuario);

                // A stable profile folder means frequency capping actually works and
                // the same machine is not counted as a brand new visitor every run.
                CoreWebView2Environment ambiente = await CoreWebView2Environment.CreateAsync(null, dadosDoUsuario);
                if (desistiu)
                    return;

                await navegador.EnsureCoreWebView2Async(ambiente);
                if (desistiu)
                    return;

                CoreWebView2 nucleo = navegador.CoreWebView2;
                nucleo.Settings.AreDefaultContextMenusEnabled = false;
                nucleo.Settings.AreDevToolsEnabled = false;
                nucleo.Settings.IsStatusBarEnabled = false;
                nucleo.Settings.IsZoomControlEnabled = false;
                nucleo.Settings.IsPasswordAutosaveEnabled = false;
                nucleo.Settings.IsGeneralAutofillEnabled = false;

                nucleo.NewWindowRequested += AbrirForaDoPrograma;
                nucleo.NavigationStarting += ManterOrigem;
                nucleo.WebMessageReceived += AoResponderAPagina;
                nucleo.ProcessFailed += (remetente, argumentos) => Desistir();
                nucleo.NavigationCompleted += (remetente, argumentos) =>
                {
                    if (!argumentos.IsSuccess)
                        Desistir();
                };

                pronto = true;
                Navegar();

                // The page confirms it actually has something to show. Silence for
                // long enough means it does not, and the strip leaves rather than
                // parking an empty rectangle at the bottom of the window.
                paciencia = new Timer { Interval = 15000 };
                paciencia.Tick += (remetente, argumentos) =>
                {
                    paciencia.Stop();
                    if (!confirmado)
                        Desistir();
                };
                paciencia.Start();
            }
            catch
            {
                // Missing runtime, locked profile folder, sandboxed process: all of
                // them mean the same thing to the user, which is no advertising.
                Desistir();
            }
        }

        private void Navegar()
        {
            if (desistiu || !pronto || origem == null)
                return;
            try
            {
                string separador = string.IsNullOrEmpty(origem.Query) ? "?" : "&";
                navegador.CoreWebView2.Navigate(
                    origem.AbsoluteUri + separador + "tema=" + (Tema.Escuro ? "escuro" : "claro"));
            }
            catch
            {
                Desistir();
            }
        }

        /// <summary>
        /// The page reports whether the placement rendered anything at all. An
        /// empty slot is treated exactly like a missing runtime: no strip.
        /// </summary>
        private void AoResponderAPagina(object remetente, CoreWebView2WebMessageReceivedEventArgs argumentos)
        {
            string recado;
            try { recado = argumentos.TryGetWebMessageAsString(); }
            catch { return; }

            if (recado == "vazio")
            {
                Desistir();
                return;
            }
            if (recado == "ok")
            {
                if (confirmado)
                    return;
                confirmado = true;
                Mostrar();
            }
        }

        private void Mostrar()
        {
            if (desistiu)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(Mostrar));
                return;
            }

            Visible = true;
            Action disponivel = Disponivel;
            if (disponivel != null)
                disponivel();
        }

        private void AbrirForaDoPrograma(object remetente, CoreWebView2NewWindowRequestedEventArgs argumentos)
        {
            argumentos.Handled = true;
            // WebView2 leaves popup policy to its host. A banner may execute
            // window.open while loading; that is not a click and must never turn
            // into a browser window opened by the application.
            if (!argumentos.IsUserInitiated)
                return;
            AbrirNoNavegador(argumentos.Uri);
        }

        /// <summary>
        /// Keeps the strip pinned to our own page. A redirect out of it is treated
        /// as a click: it opens in the default browser and the strip stays put.
        /// </summary>
        private void ManterOrigem(object remetente, CoreWebView2NavigationStartingEventArgs argumentos)
        {
            Uri destino;
            if (!Uri.TryCreate(argumentos.Uri, UriKind.Absolute, out destino))
                return;
            if (string.Equals(destino.Host, origem.Host, StringComparison.OrdinalIgnoreCase))
                return;

            argumentos.Cancel = true;
            if (!argumentos.IsUserInitiated)
                return;
            AbrirNoNavegador(argumentos.Uri);
        }

        /// <summary>
        /// Permanently removes advertising from this process before traffic is
        /// routed through the US exit. The page is stopped and replaced with a
        /// network-free document; merely hiding the control would still allow ad
        /// scripts or an approved tag's own refresh logic to issue requests.
        ///
        /// It deliberately cannot be resumed in the same process. Reusing the
        /// direct/US toggle as an ad reload button would manufacture impressions.
        /// A later process starts a fresh placement only when the route is direct.
        /// </summary>
        public void BloquearPorRoteamento()
        {
            if (InvokeRequired)
            {
                // The caller may publish the routing toggle as soon as this method
                // returns, so crossing to the UI thread must be synchronous.
                Invoke(new Action(BloquearPorRoteamento));
                return;
            }

            if (desistiu)
                return;

            // Set this first so an Iniciar() continuation that was awaiting the
            // WebView2 environment cannot navigate after the routing switch.
            desistiu = true;

            if (paciencia != null)
                paciencia.Stop();

            if (pronto && navegador.CoreWebView2 != null)
            {
                CoreWebView2 nucleo = navegador.CoreWebView2;
                nucleo.NewWindowRequested -= AbrirForaDoPrograma;
                nucleo.NavigationStarting -= ManterOrigem;
                nucleo.WebMessageReceived -= AoResponderAPagina;
                try { nucleo.Stop(); }
                catch { }
                try { nucleo.Navigate("about:blank"); }
                catch { }

                // Dispose the controller as the final boundary. This process will
                // never show the placement again, so retaining a hidden live page
                // would provide no benefit and would weaken the no-network rule.
                navegador.Dispose();
            }

            pronto = false;
            Visible = false;
            Action indisponivel = Indisponivel;
            if (indisponivel != null)
                indisponivel();
        }

        public static void AbrirNoNavegador(string url)
        {
            try
            {
                Uri destino;
                if (!Uri.TryCreate(url, UriKind.Absolute, out destino))
                    return;
                if (destino.Scheme != Uri.UriSchemeHttp && destino.Scheme != Uri.UriSchemeHttps)
                    return;
                Process.Start(new ProcessStartInfo(destino.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // No browser, no handler, no problem worth a dialog.
            }
        }

        private void Desistir()
        {
            if (desistiu)
                return;
            desistiu = true;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(Desistir));
                return;
            }

            Visible = false;
            Action indisponivel = Indisponivel;
            if (indisponivel != null)
                indisponivel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (paciencia != null)
                    paciencia.Dispose();
                navegador.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// The program ships as a single file, so the native loader WebView2 needs
        /// has nowhere to sit next to the executable. It travels as a resource and
        /// is written out once per version into the user's own folder, then loaded
        /// by full path so every later P/Invoke resolves to it.
        /// </summary>
        private static class CarregadorNativo
        {
            private const string Nome = "WebView2Loader.dll";
            private static bool tentou;
            private static bool disponivel;

            public static bool Preparar()
            {
                if (tentou)
                    return disponivel;
                tentou = true;

                try
                {
                    byte[] conteudo = Resources.WebView2Loader;
                    if (conteudo == null || conteudo.Length == 0)
                        return false;

                    string pasta = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "liberar.live", "runtime");
                    Directory.CreateDirectory(pasta);
                    string caminho = Path.Combine(pasta, Nome);

                    // Rewrite only when the bytes differ, so a copy already mapped
                    // by a running instance is never overwritten underneath it.
                    if (!File.Exists(caminho) || new FileInfo(caminho).Length != conteudo.Length)
                        File.WriteAllBytes(caminho, conteudo);

                    disponivel = LoadLibrary(caminho) != IntPtr.Zero;
                }
                catch
                {
                    disponivel = false;
                }
                return disponivel;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibrary(string caminho);
        }
    }
}
