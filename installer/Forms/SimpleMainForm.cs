using Droute.Core;
using Droute.Installer.Classes;
using Droute.Installer.Properties;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Droute.Installer.Forms
{
    /// <summary>
    /// The whole program, in one window.
    ///
    /// It is built around a single question and a single answer: what is going on
    /// right now, and what is the one thing to press. Everything else, the
    /// readiness list, the log, the advertising strip, sits below that line and
    /// never competes with it. The visual language is the landing page's, down to
    /// the tokens, so arriving from the site does not feel like arriving anywhere
    /// else.
    /// </summary>
    internal sealed class SimpleMainForm : Form
    {
        private enum Estado { SemDiscord, NaoInstalado, Desatualizado, Direta, Ativa, Ocupado }

        private const int Largura = 420;
        private const int Margem = 16;
        private const int AlturaCabecalho = 50;

        private readonly MarcaApp marca;
        private readonly BotaoIcone botaoTema;
        private readonly BotaoIcone botaoMenu;
        private readonly BotaoIcone botaoMinimizar;
        private readonly BotaoIcone botaoFechar;
        private readonly ToolTip dicas;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem itemReinstalar;
        private readonly ToolStripMenuItem itemRemover;
        private readonly ToolStripMenuItem itemDetalhes;

        private readonly CartaoEstado cartao;
        private readonly BlocoVerificacao verificacao;
        private readonly PainelAnuncio anuncio;

        private readonly Font fonteMarca = Tema.FonteMedia(11.5f);
        private readonly Timer relogio;
        private readonly List<string> registro = new List<string>();
        private readonly object travaRegistro = new object();

        private Estado estado;
        private bool ocupado;
        private bool temAlgumPatch;
        private int carencia;
        private Situacao situacaoServidor = Situacao.Verificando;
        private AlcanceServidor alcanceServidor = AlcanceServidor.Ok;
        // The window opens at the size it can justify. Room for the strip is only
        // taken once the page confirms it filled the slot, so the common failure
        // (no WebView2 runtime, page unreachable, empty placement) costs nothing
        // instead of showing a tall window that collapses half a second later.
        private bool comAnuncio;
        private bool conferindoServidor;

        public SimpleMainForm()
        {
            Text = "liberar.live";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = Tema.Fonte(9f);
            BackColor = Tema.Fundo;
            ClientSize = new Size(Largura, 600);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            marca = new MarcaApp { Location = new Point(18, (AlturaCabecalho - 24) / 2) };
            botaoTema = new BotaoIcone { Location = new Point(276, 10) };
            botaoMenu = new BotaoIcone { Glifo = Icone.Menu, Location = new Point(310, 10) };
            botaoMinimizar = new BotaoIcone { Glifo = Icone.Minimizar, Location = new Point(344, 10) };
            botaoFechar = new BotaoIcone { Glifo = Icone.Fechar, Location = new Point(378, 10), Perigoso = true };

            dicas = new ToolTip { AutomaticDelay = 400, InitialDelay = 400, ReshowDelay = 120, AutoPopDelay = 3000 };
            dicas.SetToolTip(botaoMenu, "opções");
            dicas.SetToolTip(botaoMinimizar, "minimizar");
            dicas.SetToolTip(botaoFechar, "fechar");

            cartao = new CartaoEstado { Location = new Point(Margem, AlturaCabecalho + 8), Width = Largura - Margem * 2 };
            verificacao = new BlocoVerificacao { Width = Largura - Margem * 2 };
            anuncio = new PainelAnuncio { Width = Largura };

            menu = CriarMenu();
            itemReinstalar = new ToolStripMenuItem("reinstalar");
            itemRemover = new ToolStripMenuItem("remover");
            itemDetalhes = new ToolStripMenuItem("detalhes");
            menu.Items.Add(itemReinstalar);
            menu.Items.Add(itemRemover);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemDetalhes);

            cartao.Botao.Click += async (remetente, argumentos) => await AcaoPrincipal();
            cartao.ErroClicado += MostrarDetalhes;
            cartao.Remedido += Remedir;
            verificacao.Remedido += Remedir;
            anuncio.Disponivel += () => { comAnuncio = true; Remedir(); };
            anuncio.Indisponivel += () => { comAnuncio = false; Remedir(); };

            botaoTema.Click += (remetente, argumentos) => Tema.Alternar();
            botaoMenu.Click += (remetente, argumentos) =>
                menu.Show(botaoMenu, new Point(botaoMenu.Width - menu.PreferredSize.Width, botaoMenu.Height + 6));
            botaoMinimizar.Click += (remetente, argumentos) => WindowState = FormWindowState.Minimized;
            botaoFechar.Click += (remetente, argumentos) => Close();
            itemReinstalar.Click += async (remetente, argumentos) => await AcaoDeInstalacao(true);
            itemRemover.Click += async (remetente, argumentos) => await AcaoDeInstalacao(false);
            itemDetalhes.Click += (remetente, argumentos) => MostrarDetalhes();
            FormClosing += AoFechar;
            Tema.Mudou += AplicarTema;

            relogio = new Timer { Interval = 250 };
            relogio.Tick += AoPassarOTempo;
            relogio.Start();

            MouseDown += ArrastarJanela;
            marca.MouseDown += ArrastarJanela;

            Controls.Add(marca);
            Controls.Add(botaoTema);
            Controls.Add(botaoMenu);
            Controls.Add(botaoMinimizar);
            Controls.Add(botaoFechar);
            Controls.Add(cartao);
            Controls.Add(verificacao);
            Controls.Add(anuncio);

            AplicarTema();
            RecalcularEstado();
            ConferirServidor();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int SombraDeClasse = 0x00020000;
                CreateParams parametros = base.CreateParams;
                parametros.ClassStyle |= SombraDeClasse;
                return parametros;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Remedir();
            ArredondarJanela();
            // Advertising is direct-route inventory only. If this process opens
            // during an active US session, do not even initialize the WebView.
            if (!RuntimeToggle.IsEnabled())
                anuncio.Iniciar();
        }

        // ------------------------------------------------------------- aparência

        private void AplicarTema()
        {
            BackColor = Tema.Fundo;
            botaoTema.Glifo = Tema.Escuro ? Icone.Sol : Icone.Lua;
            dicas.SetToolTip(botaoTema, Tema.Escuro ? "tema claro" : "tema escuro");

            cartao.AplicarTema();
            verificacao.BackColor = Tema.Fundo;
            anuncio.AplicarTema();

            menu.BackColor = Tema.Elevado;
            menu.ForeColor = Tema.Texto;

            foreach (Control controle in Controls)
                controle.Invalidate();
            Invalidate();
        }

        private void Remedir()
        {
            verificacao.Location = new Point(Margem, cartao.Bottom + 8);

            int altura;
            // Deliberately not anuncio.Visible: that reports effective visibility,
            // which is false for every child while the window itself has not been
            // shown yet. Reading it here laid the whole program out as if there
            // were no strip, and left the strip sitting at the top left corner.
            // The flag is owned by the strip's own two events instead.
            if (comAnuncio)
            {
                anuncio.Location = new Point(0, verificacao.Bottom + 12);
                altura = anuncio.Bottom;
            }
            else
            {
                altura = verificacao.Bottom + 16;
            }

            if (ClientSize.Height != altura)
            {
                ClientSize = new Size(Largura, altura);
                ArredondarJanela();
            }
            ManterNaTela();
        }

        /// <summary>
        /// The window grows when the readiness list opens, and it opens by itself
        /// when something is wrong. Without this it would grow straight off the
        /// bottom of the screen at the exact moment it has something to say.
        /// </summary>
        private void ManterNaTela()
        {
            if (!IsHandleCreated)
                return;

            Rectangle util = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(util.Left, Math.Min(Left, util.Right - Width));
            int y = Math.Max(util.Top, Math.Min(Top, util.Bottom - Height));
            if (x != Left || y != Top)
                Location = new Point(x, y);
        }

        private void ArredondarJanela()
        {
            using (GraphicsPath caminho = Desenho.Arredondar(new Rectangle(0, 0, Width, Height), 16))
            {
                Region anterior = Region;
                Region = new Region(caminho);
                if (anterior != null)
                    anterior.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, "liberar.live", fonteMarca,
                new Rectangle(50, 0, 200, AlturaCabecalho), Tema.Texto,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------- estado

        private async Task AcaoPrincipal()
        {
            switch (estado)
            {
                case Estado.NaoInstalado:
                case Estado.Desatualizado:
                    await AcaoDeInstalacao(true);
                    break;
                case Estado.Direta:
                    if (!JanelaConfirmacao.Perguntar(this))
                        break;
                    await Alternar();
                    break;
                case Estado.Ativa:
                    await Alternar();
                    break;
            }
        }

        private void RecalcularEstado()
        {
            List<DiscordManager.Branches> encontradas = DiscordManager.GetInstalledBranches();
            List<DiscordManager.Branches> instaladas = encontradas.Where(EstaInstalado).ToList();
            List<DiscordManager.Branches> atuais = encontradas.Where(EstaAtual).ToList();

            temAlgumPatch = instaladas.Count > 0;
            itemReinstalar.Enabled = !ocupado && encontradas.Count > 0;
            itemRemover.Enabled = !ocupado && temAlgumPatch;
            botaoMenu.Enabled = !ocupado;

            verificacao.Atualizar(Verificacao.Montar(
                encontradas.Count, instaladas.Count, atuais.Count, situacaoServidor, alcanceServidor));

            if (ocupado)
                AplicarEstado(Estado.Ocupado);
            else if (encontradas.Count == 0)
                AplicarEstado(Estado.SemDiscord);
            else if (instaladas.Count == 0)
                AplicarEstado(Estado.NaoInstalado);
            else if (instaladas.Count != encontradas.Count || atuais.Count != encontradas.Count)
                AplicarEstado(Estado.Desatualizado);
            else
                AplicarEstado(RuntimeToggle.IsEnabled() ? Estado.Ativa : Estado.Direta);
        }

        private void AplicarEstado(Estado novo)
        {
            estado = novo;
            cartao.DefinirErro(null);
            cartao.Botao.Enabled = novo != Estado.SemDiscord && novo != Estado.Ocupado;

            switch (novo)
            {
                case Estado.SemDiscord:
                    cartao.Definir(
                        "discord não encontrado",
                        "não achamos o discord neste computador. instale o discord, abra ele uma vez e volte aqui.",
                        Tema.Apagado, false, false);
                    cartao.Botao.Text = "instalar no discord";
                    cartao.DefinirTempo(null, 0);
                    break;

                case Estado.NaoInstalado:
                    cartao.Definir(
                        "quase lá",
                        "falta preparar o seu discord. o discord vai fechar e abrir sozinho, e isso leva alguns segundos. nada é adicionado ao windows.",
                        Tema.Apagado, false, false);
                    cartao.Botao.Text = "instalar no discord";
                    cartao.DefinirTempo(null, 0);
                    break;

                case Estado.Desatualizado:
                    cartao.Definir(
                        "atualização pendente",
                        "saiu uma versão nova. atualize para a câmera e a transmissão continuarem voltando quando você precisar.",
                        Tema.Temporario, false, false);
                    cartao.Botao.Text = "atualizar";
                    cartao.DefinirTempo(null, 0);
                    break;

                case Estado.Direta:
                    cartao.Definir(
                        "conexão normal",
                        "o discord detecta que sua localização está vindo do:",
                        // The dot answers "can I press this right now", so it waits on
                        // the server the same way the readiness list does. A green dot
                        // over a list that says something is missing is a lie.
                        situacaoServidor == Situacao.Ok ? Tema.Pronto : Tema.Temporario,
                        false, false);
                    cartao.DefinirOrigem(Bandeira.Brasil, "Brasil",
                        "ao liberar, só a conexão do discord usa uma rota VPN-style pelos nossos servidores nos estados unidos, o processo é automático, sem afetar as outras conexões do computador.");
                    cartao.Botao.Text = "liberar transmissão/webcam";
                    cartao.DefinirTempo(null, 0);
                    break;

                case Estado.Ativa:
                    cartao.Definir(
                        "liberação ativa",
                        "a conexão do discord está saindo agora por:",
                        Tema.Temporario, true, false);
                    cartao.DefinirOrigem(Bandeira.EstadosUnidos, "Estados Unidos",
                        "abra sua transmissão ou ligue sua webcam agora, ou entre na de outra pessoa. assim que funcionar, clique em voltar à conexão normal para tirar o atraso e ficar com o seu ping de sempre, sem reiniciar o discord.");
                    cartao.Botao.Text = "voltar à conexão normal";
                    AtualizarContagem();
                    break;

                default:
                    cartao.Definir("aplicando", "só um instante.", Tema.Apagado, false, true);
                    cartao.Botao.Text = "aguarde";
                    cartao.DefinirTempo(null, 0);
                    break;
            }
        }

        /// <summary>
        /// Replaces the card's copy mid-operation without leaving the busy state,
        /// so a long step explains itself instead of showing a spinner and nothing.
        /// </summary>
        private void Narrar(string titulo, string descricao)
        {
            cartao.Definir(titulo, descricao, Tema.Apagado, false, true);
            cartao.Botao.Text = "aguarde";
        }

        private static bool EstaInstalado(DiscordManager.Branches ramo)
        {
            try
            {
                string raiz = DiscordManager.GetBranchRoot(ramo);
                string pasta = DiscordManager.GetLastVersionPath(raiz);
                return Droute.Core.Droute.IsInstalled(pasta) &&
                       File.Exists(Droute.Core.Droute.GetUpdaterHookPath(raiz)) &&
                       File.Exists(Droute.Core.Droute.GetUpdaterConfigPath(raiz));
            }
            catch { return false; }
        }

        private static bool EstaAtual(DiscordManager.Branches ramo)
        {
            try
            {
                if (!EstaInstalado(ramo))
                    return false;

                string raiz = DiscordManager.GetBranchRoot(ramo);
                string pasta = DiscordManager.GetLastVersionPath(raiz);
                return File.ReadAllBytes(Droute.Core.Droute.GetPayloadPath(pasta)).SequenceEqual(Resources.Droute64) &&
                       File.ReadAllBytes(Droute.Core.Droute.GetUpdaterHookPath(raiz)).SequenceEqual(Resources.UpdaterHook) &&
                       File.ReadAllText(Droute.Core.Droute.GetUpdaterConfigPath(raiz)) == Resources.UpdaterConfig;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------- ações

        private async Task Alternar()
        {
            bool ligar = !RuntimeToggle.IsEnabled();
            string erro = null;
            bool demorouParaAbrir = false;
            try
            {
                DefinirOcupado(true);

                // A full process restart gives us an observable, reliable boundary:
                // every old PID exits before a new responsive Discord window appears.
                // Disabling still preserves the authorized stream and country state.
                if (ligar)
                {
                    Narrar("preparando a liberação", "pedindo a sua liberação ao servidor, isso leva alguns segundos.");

                    bool primeiraVez = !File.Exists(DeviceIdentity.StorePath);
                    if (primeiraVez)
                        Anotar("Primeiro uso: registrando este computador no servidor.");

                    // The credential is issued per activation and expires on the
                    // server, so nothing durable is stored and nothing is shared
                    // with any other installation.
                    SessionCredential credencial = await Task.Run(() =>
                    {
                        using (DeviceIdentity identidade = DeviceIdentity.LoadOrCreate())
                            return new BrokerClient().RequestSession(identidade, System.Threading.CancellationToken.None);
                    });

                    situacaoServidor = Situacao.Ok;
                    // Stop and unload every advertising resource before publishing
                    // the US route. Hiding the panel alone would leave its scripts
                    // free to perform network requests in the background.
                    anuncio.BloquearPorRoteamento();
                    RuntimeToggle.Activate(credencial);
                    Anotar("Credencial temporária recebida; o servidor a descarta em " +
                           Math.Max(1, (int)credencial.ExpiresIn.TotalMinutes) + " min.");
                    Anotar("Liberação ativada por no máximo 5 minutos.");

                    List<DiscordManager.Branches> instaladas = DiscordManager.GetInstalledBranches();
                    List<DiscordManager.Branches> reabrir = instaladas.Where(DiscordManager.IsDiscordRunning).ToList();
                    if (reabrir.Count == 0)
                        reabrir.Add(instaladas.Contains(DiscordManager.Branches.Stable) ? DiscordManager.Branches.Stable : instaladas[0]);

                    Narrar("reiniciando o discord", "fechando e abrindo o discord para ele pegar a liberação. não feche esta janela.");
                    bool pronto = await Task.Run(() => DiscordTools.RestartAndWait(reabrir));
                    if (pronto)
                        Anotar("Discord reiniciado e pronto para transmitir.");
                    else
                    {
                        demorouParaAbrir = true;
                        Anotar("O Discord foi aberto, mas a janela ainda não ficou pronta.");
                    }
                }
                else
                {
                    RuntimeToggle.SetEnabled(false);
                    Anotar("Conexão direta restaurada.");
                    Anotar("Proxy desligado sem recarregar o Discord.");
                }
            }
            catch (Exception excecao)
            {
                // Any failure returns to the direct connection and drops the
                // credential, which the server would expire anyway.
                RuntimeToggle.SetEnabled(false);
                if (ligar)
                    situacaoServidor = Situacao.Falhou;
                erro = excecao.Message;
            }
            finally
            {
                DefinirOcupado(false);
                RecalcularEstado();
            }

            if (erro != null)
                MostrarErro(erro);
            else if (demorouParaAbrir)
                Anotar("O Discord ainda está iniciando.");

            ConferirServidor();
        }

        private async Task AcaoDeInstalacao(bool instalar)
        {
            List<DiscordManager.Branches> encontradas = DiscordManager.GetInstalledBranches();
            if (encontradas.Count == 0)
            {
                MostrarErro("não achamos o discord neste computador.");
                return;
            }

            bool primeiraInstalacao = instalar && !encontradas.Any(EstaInstalado);
            List<DiscordManager.Branches> reabrir = encontradas.Where(DiscordManager.IsDiscordRunning).ToList();
            if (reabrir.Count == 0)
                reabrir.Add(encontradas.Contains(DiscordManager.Branches.Stable) ? DiscordManager.Branches.Stable : encontradas[0]);

            DefinirOcupado(true);
            Narrar(instalar ? "preparando o seu discord" : "removendo do discord",
                   instalar
                       ? "o discord vai fechar e abrir sozinho. não feche esta janela."
                       : "devolvendo o discord ao normal. ele vai fechar e abrir sozinho.");

            PatchTools.OnLog += Anotar;
            bool sucesso = false;
            try
            {
                sucesso = await Task.Run(() =>
                {
                    foreach (DiscordManager.Branches ramo in encontradas)
                    {
                        if (!DiscordTools.CloseWait(ramo, 10000))
                            throw new InvalidOperationException("Não foi possível fechar " + NomeDoRamo(ramo) + ".");
                    }

                    bool todosOk = true;
                    foreach (DiscordManager.Branches ramo in encontradas)
                        todosOk &= instalar ? PatchTools.Install(ramo) : PatchTools.Remove(ramo);

                    if (!todosOk)
                        return false;

                    if (instalar)
                    {
                        if (primeiraInstalacao)
                            RuntimeToggle.SetEnabled(false);
                    }
                    else
                    {
                        // Removing the patch also discards the device identity, so
                        // nothing is left behind that could be reused later.
                        RuntimeToggle.Purge();
                    }

                    foreach (DiscordManager.Branches ramo in reabrir)
                        DiscordManager.Launch(ramo);
                    return true;
                });
            }
            catch (Exception excecao)
            {
                MostrarErro(excecao.Message);
            }
            finally
            {
                PatchTools.OnLog -= Anotar;
                DefinirOcupado(false);
                RecalcularEstado();
            }

            if (!sucesso)
                MostrarErro("não deu para concluir. clique aqui para ver os detalhes.");
        }

        private void DefinirOcupado(bool novo)
        {
            ocupado = novo;
            cartao.Botao.Enabled = !novo;
            botaoMenu.Enabled = !novo;
            itemReinstalar.Enabled = !novo;
            itemRemover.Enabled = !novo && temAlgumPatch;
            if (!novo)
                RecalcularEstado();
        }

        private void MostrarErro(string mensagem)
        {
            Anotar("ERRO: " + mensagem);
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(MostrarErro), mensagem);
                return;
            }
            cartao.DefinirErro(mensagem);
        }

        private void MostrarDetalhes()
        {
            string conteudo;
            lock (travaRegistro)
                conteudo = registro.Count == 0 ? "nenhum detalhe disponível ainda." : string.Join(Environment.NewLine, registro);
            using (var janela = new JanelaDetalhes(conteudo))
                janela.ShowDialog(this);
        }

        private void Anotar(string mensagem)
        {
            lock (travaRegistro)
            {
                registro.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + mensagem);
                if (registro.Count > 300)
                    registro.RemoveAt(0);
            }
        }

        // ------------------------------------------------------------- servidor

        /// <summary>
        /// Probes the broker so the checklist can say whether activating would work
        /// at all. It is a background courtesy: nothing in the program waits on it,
        /// and a failure here never blocks the button.
        /// </summary>
        private async void ConferirServidor()
        {
            if (conferindoServidor)
                return;
            conferindoServidor = true;

            AlcanceServidor alcance = await Task.Run(() => new BrokerClient().IsReachable());

            conferindoServidor = false;
            carencia = 0;
            alcanceServidor = alcance;
            situacaoServidor = alcance == AlcanceServidor.Ok ? Situacao.Ok : Situacao.Falhou;
            if (!IsDisposed)
                RecalcularEstado();
        }

        // ------------------------------------------------------------- tempo

        private void AoPassarOTempo(object remetente, EventArgs argumentos)
        {
            // The checklist goes stale if nobody ever asks again, so the probe
            // repeats while the window sits idle, at a pace nobody would notice.
            carencia++;
            if (!ocupado && carencia > 1200)
                ConferirServidor();

            if (ocupado || estado != Estado.Ativa)
                return;

            if (RuntimeToggle.IsEnabled())
            {
                AtualizarContagem();
                return;
            }

            Anotar("Limite de 5 minutos atingido; proxy desligado automaticamente.");
            RecalcularEstado();
            cartao.Definir(
                "conexão normal",
                "a liberação chegou ao fim dos 5 minutos e desligou sozinha. o seu ping voltou ao normal. clique de novo quando precisar transmitir.",
                Tema.Pronto, false, false);
            cartao.Botao.Text = "liberar transmissão/webcam";
            cartao.DefinirTempo(null, 0);
        }

        private void AtualizarContagem()
        {
            TimeSpan restante = RuntimeToggle.GetRemaining();
            int segundos = Math.Max(0, (int)Math.Ceiling(restante.TotalSeconds));
            string relogioTexto = string.Format("{0:00}:{1:00}", segundos / 60, segundos % 60);
            cartao.DefinirTempo(
                segundos <= 60
                    ? "último minuto, volta sozinho em " + relogioTexto
                    : "volta sozinho em " + relogioTexto,
                RuntimeToggle.ActivationDuration.TotalSeconds <= 0
                    ? 0
                    : segundos / RuntimeToggle.ActivationDuration.TotalSeconds);
        }

        // ------------------------------------------------------------- janela

        private static string NomeDoRamo(DiscordManager.Branches ramo)
        {
            switch (ramo)
            {
                case DiscordManager.Branches.Stable: return "Discord";
                case DiscordManager.Branches.Canary: return "Discord Canary";
                case DiscordManager.Branches.PTB: return "Discord PTB";
                default: return ramo.ToString();
            }
        }

        private ContextMenuStrip CriarMenu()
        {
            return new ContextMenuStrip
            {
                BackColor = Tema.Cartao,
                ForeColor = Tema.Texto,
                Font = Tema.Fonte(9f),
                ShowImageMargin = false,
                Padding = new Padding(6),
                Renderer = new ToolStripProfessionalRenderer(new CoresMenu())
            };
        }

        private void AoFechar(object remetente, FormClosingEventArgs argumentos)
        {
            if (ocupado)
            {
                argumentos.Cancel = true;
                return;
            }
            relogio.Stop();
            Tema.Mudou -= AplicarTema;
        }

        private void ArrastarJanela(object remetente, MouseEventArgs argumentos)
        {
            if (argumentos.Button != MouseButtons.Left)
                return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr janela, int mensagem, IntPtr wParam, IntPtr lParam);
    }
}
