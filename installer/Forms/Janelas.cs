using Droute.Installer.Classes;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Droute.Installer.Forms
{
    /// <summary>
    /// Shared chrome for the small windows: no system border, rounded corners, a
    /// hairline outline and the whole surface draggable. It exists so the dialogs
    /// look like they came from the same place as the main window rather than
    /// from Windows.
    /// </summary>
    internal abstract class JanelaLisa : Form
    {
        protected JanelaLisa()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Tema.Elevado;
            Font = Tema.Fonte(9f);
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            MouseDown += Arrastar;
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
            using (GraphicsPath caminho = Desenho.Arredondar(new Rectangle(0, 0, Width, Height), 16))
                Region = new Region(caminho);
            CentralizarSobreDono();
        }

        /// <summary>Height of the drag strip at the top, or zero for none.</summary>
        protected virtual int AlturaCabecalho { get { return 0; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (AlturaCabecalho > 0)
            {
                // Clipped to the window's own shape, so the band keeps the rounded
                // top corners instead of squaring them off.
                GraphicsState estado = g.Save();
                using (GraphicsPath forma = Desenho.Arredondar(new Rectangle(0, 0, Width, Height), 16))
                {
                    g.SetClip(forma, CombineMode.Intersect);
                    using (var pincel = new SolidBrush(Tema.Cabecalho))
                        g.FillRectangle(pincel, 0, 0, Width, AlturaCabecalho);
                }
                g.Restore(estado);

                using (var caneta = new Pen(Tema.Linha))
                    g.DrawLine(caneta, 0, AlturaCabecalho, Width, AlturaCabecalho);
            }

            using (GraphicsPath caminho = Desenho.Arredondar(new Rectangle(1, 1, Width - 3, Height - 3), 15))
            using (var caneta = new Pen(Tema.LinhaForte, 1.4f))
                g.DrawPath(caneta, caminho);
        }

        protected override bool ProcessCmdKey(ref Message mensagem, Keys tecla)
        {
            if (tecla == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref mensagem, tecla);
        }

        protected void Arrastar(object remetente, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(0x0002), IntPtr.Zero);
        }

        private void CentralizarSobreDono()
        {
            Form dono = Owner;
            Rectangle limites = dono == null ? Screen.FromPoint(Cursor.Position).WorkingArea : dono.Bounds;
            Rectangle util = dono == null ? limites : Screen.FromControl(dono).WorkingArea;

            int x = limites.Left + (limites.Width - Width) / 2;
            int y = limites.Top + (limites.Height - Height) / 2;
            Location = new Point(
                Math.Max(util.Left, Math.Min(x, util.Right - Width)),
                Math.Max(util.Top, Math.Min(y, util.Bottom - Height)));
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr janela, int mensagem, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// A small themed checkbox: a rounded box and a quiet label. It exists only
    /// for the "não mostrar novamente" option on the confirmation dialog, so it
    /// carries no more state than that one use needs. Clicking anywhere on it
    /// toggles, which keeps the hit target as wide as the row.
    /// </summary>
    internal sealed class CaixaSelecao : ControlePintado
    {
        private bool sobre;
        public bool Marcado { get; private set; }

        public CaixaSelecao()
        {
            Cursor = Cursors.Hand;
            Height = 22;
        }

        protected override void OnMouseEnter(EventArgs e) { sobre = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sobre = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Marcado = !Marcado; Invalidate(); base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int lado = 18;
            int topo = (Height - lado) / 2;
            Rectangle caixa = new Rectangle(0, topo, lado, lado);

            using (GraphicsPath caminho = Desenho.Arredondar(caixa, 5))
            {
                if (Marcado)
                    using (var pincel = new SolidBrush(Tema.BotaoFundo))
                        g.FillPath(pincel, caminho);
                else
                    using (var caneta = new Pen(sobre ? Tema.Apagado : Tema.LinhaForte, 1.5f))
                        g.DrawPath(caneta, caminho);
            }

            if (Marcado)
                using (var caneta = new Pen(Tema.BotaoTexto, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(caneta, new[]
                    {
                        new Point(caixa.Left + 4, caixa.Top + 9),
                        new Point(caixa.Left + 8, caixa.Top + 13),
                        new Point(caixa.Left + 14, caixa.Top + 5)
                    });

            Rectangle rotulo = new Rectangle(lado + 10, 0, Width - lado - 10, Height);
            TextRenderer.DrawText(g, Text, Font, rotulo, sobre ? Tema.Texto : Tema.Apagado,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// The one confirmation in the program.
    ///
    /// It is here because activating costs something the user cannot see coming:
    /// Discord restarts under them, and their ping goes up for as long as the
    /// release lasts. Saying so before the click is what makes the higher ping
    /// read as the price of the feature instead of as the program breaking.
    /// </summary>
    internal sealed class JanelaConfirmacao : JanelaLisa
    {
        private const int Largura = 384;
        private const int Recuo = 24;
        private const int Faixa = 52;

        // Three facts and nothing else: the restart, the ping, and the way back.
        // Anything longer stops being read, and this is the one screen where being
        // read actually matters.
        private const string Corpo =
            "o discord vai fechar e abrir sozinho, se estiver numa chamada, entre nela de novo depois.\n\n" +
            "a conexão do discord passa a sair por um servidor nosso nos estados unidos, é assim que a transmissão e a webcam voltam, e é de graça.\n\n" +
            "o seu ping sobe até você retornar para a conexão normal, ou após os 5 minutos máximos de conexão acabarem.";

        private readonly Font fonteTitulo = Tema.FonteMedia(13f);
        private readonly Font fonteCorpo = Tema.Fonte(9.5f);

        private JanelaConfirmacao()
        {
            Text = "antes de continuar";

            int larguraTexto = Largura - Recuo * 2;
            int alturaCorpo = Desenho.Altura(Corpo, fonteCorpo, larguraTexto);
            int topoCaixa = Faixa + 20 + alturaCorpo + 14;
            int alturaCaixa = 22;
            int topoBotoes = topoCaixa + alturaCaixa + 18;
            ClientSize = new Size(Largura, topoBotoes + 44 + Recuo);

            var fechar = new BotaoIcone
            {
                Glifo = Icone.Fechar,
                Location = new Point(Largura - Recuo - 30 + 8, (Faixa - 30) / 2),
                Perigoso = true,
                Assento = Tema.Cabecalho
            };

            int metade = (larguraTexto - 10) / 2;
            var cancelar = new BotaoVazado
            {
                Font = Tema.Fonte(9.5f),
                Text = "cancelar"
            };
            cancelar.SetBounds(Recuo, topoBotoes, metade, 44);

            var confirmar = new BotaoPrimario
            {
                Font = Tema.FonteMedia(9.5f),
                Text = "reiniciar o discord"
            };
            confirmar.SetBounds(Recuo + metade + 10, topoBotoes, larguraTexto - metade - 10, 44);

            var naoMostrar = new CaixaSelecao
            {
                Font = Tema.Fonte(9f),
                Text = "não mostrar novamente"
            };
            naoMostrar.SetBounds(Recuo, topoCaixa, larguraTexto, alturaCaixa);

            fechar.Click += (remetente, argumentos) => { DialogResult = DialogResult.Cancel; Close(); };
            cancelar.Click += (remetente, argumentos) => { DialogResult = DialogResult.Cancel; Close(); };
            confirmar.Click += (remetente, argumentos) =>
            {
                if (naoMostrar.Marcado)
                    DefinirPular(true);
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.Add(fechar);
            Controls.Add(cancelar);
            Controls.Add(confirmar);
            Controls.Add(naoMostrar);
        }

        public static bool Perguntar(IWin32Window dono)
        {
            // Marcar "não mostrar novamente" grava uma preferência local (o mesmo
            // ramo Software\droute que o desinstalar apaga). Não é controle de
            // segurança: a ativação real continua limitada pela credencial no servidor.
            if (DevePular())
                return true;
            using (var janela = new JanelaConfirmacao())
                return janela.ShowDialog(dono) == DialogResult.OK;
        }

        private const string CaminhoRegistro = @"Software\droute";
        private const string ValorPular = "skip_confirmacao";

        private static bool DevePular()
        {
            using (RegistryKey chave = Registry.CurrentUser.OpenSubKey(CaminhoRegistro, false))
                return chave?.GetValue(ValorPular, 0) is int marcado && marcado != 0;
        }

        private static void DefinirPular(bool pular)
        {
            using (RegistryKey chave = Registry.CurrentUser.CreateSubKey(CaminhoRegistro))
                chave.SetValue(ValorPular, pular ? 1 : 0, RegistryValueKind.DWord);
        }

        protected override int AlturaCabecalho { get { return Faixa; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int larguraTexto = Largura - Recuo * 2;

            TextRenderer.DrawText(e.Graphics, "antes de continuar", fonteTitulo,
                new Rectangle(Recuo, 0, larguraTexto - 34, Faixa), Tema.Texto,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(e.Graphics, Corpo, fonteCorpo,
                new Rectangle(Recuo, Faixa + 20, larguraTexto, Desenho.Altura(Corpo, fonteCorpo, larguraTexto)),
                Tema.Apagado, Desenho.Corrido);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fonteTitulo.Dispose();
                fonteCorpo.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The log, for when something failed and the short message on the card is not
    /// enough. It is a plain transcript on purpose: it gets pasted into a message
    /// asking for help far more often than it gets read here.
    /// </summary>
    internal sealed class JanelaDetalhes : JanelaLisa
    {
        private readonly Font fonteTitulo = Tema.FonteMedia(11f);
        private readonly TextBox transcricao;

        public JanelaDetalhes(string conteudo)
        {
            Text = "detalhes";
            ClientSize = new Size(560, 400);

            transcricao = new TextBox
            {
                BackColor = Tema.Fundo,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f),
                ForeColor = Tema.Apagado,
                Location = new Point(21, 70),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Size = new Size(518, 266),
                Text = conteudo,
                // The longest lines here are the failure messages, which are the
                // ones worth reading. Without wrapping they run past the right edge
                // of a box that only scrolls vertically, so they cannot be read at
                // all.
                WordWrap = true
            };

            var fechar = new BotaoIcone
            {
                Glifo = Icone.Fechar,
                Location = new Point(514, 11),
                Perigoso = true,
                Assento = Tema.Cabecalho
            };
            var copiar = new BotaoVazado { Font = Tema.Fonte(9.5f), Text = "copiar" };
            copiar.SetBounds(20, 350, 130, 36);
            var sair = new BotaoPrimario { Font = Tema.FonteMedia(9.5f), Text = "fechar" };
            sair.SetBounds(410, 350, 130, 36);

            fechar.Click += (remetente, argumentos) => Close();
            sair.Click += (remetente, argumentos) => Close();
            copiar.Click += (remetente, argumentos) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(transcricao.Text))
                        Clipboard.SetText(transcricao.Text);
                    copiar.Text = "copiado";
                    copiar.Invalidate();
                }
                catch
                {
                    // Another process can own the clipboard; the log is still on screen.
                }
            };

            Controls.Add(transcricao);
            Controls.Add(fechar);
            Controls.Add(copiar);
            Controls.Add(sair);
        }

        protected override int AlturaCabecalho { get { return 52; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, "detalhes", fonteTitulo,
                new Rectangle(24, 0, 300, 52), Tema.Texto,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath caixa = Desenho.Arredondar(new Rectangle(16, 56, 528, 286), 12))
            {
                using (var pincel = new SolidBrush(Tema.Fundo))
                    e.Graphics.FillPath(pincel, caixa);
                using (var caneta = new Pen(Tema.Linha))
                    e.Graphics.DrawPath(caneta, caixa);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                fonteTitulo.Dispose();
            base.Dispose(disposing);
        }
    }
}
