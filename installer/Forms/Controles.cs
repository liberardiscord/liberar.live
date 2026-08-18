using Droute.Installer.Classes;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Droute.Installer.Forms
{
    internal static class Desenho
    {
        public static GraphicsPath Arredondar(Rectangle r, int raio)
        {
            int d = raio * 2;
            var caminho = new GraphicsPath();
            if (d <= 0)
            {
                caminho.AddRectangle(r);
                return caminho;
            }
            caminho.AddArc(r.Left, r.Top, d, d, 180, 90);
            caminho.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            caminho.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            caminho.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            caminho.CloseFigure();
            return caminho;
        }

        /// <summary>
        /// Text is measured and drawn with the same flags everywhere, otherwise the
        /// height a layout reserves and the height GDI actually paints drift apart.
        /// </summary>
        public const TextFormatFlags Corrido =
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        public static int Altura(string texto, Font fonte, int largura)
        {
            if (string.IsNullOrEmpty(texto))
                return 0;
            return TextRenderer.MeasureText(texto, fonte, new Size(largura, int.MaxValue), Corrido).Height;
        }
    }

    /// <summary>Base for everything painted by hand: no flicker, repaint on resize.</summary>
    internal abstract class ControlePintado : Control
    {
        protected ControlePintado()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        /// <summary>
        /// What to paint behind this control, when the parent's own colour is not
        /// what is actually underneath it.
        ///
        /// These controls have no background of their own, so they borrow the
        /// parent's. A control sitting on a band the parent painted, like the strip
        /// at the top of a dialog, would otherwise cut a rectangle of the wrong
        /// colour out of it.
        /// </summary>
        public Color? Assento { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color atras = Assento ?? (Parent == null ? Tema.Fundo : Parent.BackColor);
            using (var pincel = new SolidBrush(atras))
                e.Graphics.FillRectangle(pincel, ClientRectangle);
        }
    }

    /// <summary>
    /// The one filled button on screen. It is the page's `.btn`: solid foreground
    /// colour, dark label, no accent hue, because colour in this interface means
    /// state and a button is not a state.
    /// </summary>
    internal sealed class BotaoPrimario : ControlePintado
    {
        private bool sobre;
        private bool pressionado;

        public BotaoPrimario()
        {
            Cursor = Cursors.Hand;
            Height = 44;
        }

        protected override void OnMouseEnter(EventArgs e) { sobre = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sobre = pressionado = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressionado = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressionado = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle area = new Rectangle(0, 0, Width - 1, Height - 1);

            Color fundo = Tema.BotaoFundo;
            Color texto = Tema.BotaoTexto;
            if (!Enabled)
            {
                fundo = Tema.RealceForte;
                texto = Tema.Fantasma;
            }
            else if (pressionado)
                fundo = Mesclar(fundo, Tema.Fundo, 0.16);
            else if (sobre)
                fundo = Mesclar(fundo, Tema.Fundo, 0.08);

            using (GraphicsPath caminho = Desenho.Arredondar(area, 11))
            using (var pincel = new SolidBrush(fundo))
                e.Graphics.FillPath(pincel, caminho);

            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, texto,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        public static Color Mesclar(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>The page's `.btn-vazado`: outlined, quiet, for the secondary choice.</summary>
    internal sealed class BotaoVazado : ControlePintado
    {
        private bool sobre;
        private bool pressionado;

        public BotaoVazado()
        {
            Cursor = Cursors.Hand;
            Height = 44;
        }

        protected override void OnMouseEnter(EventArgs e) { sobre = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sobre = pressionado = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressionado = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressionado = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle area = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath caminho = Desenho.Arredondar(area, 11))
            {
                using (var pincel = new SolidBrush(pressionado ? Tema.RealceForte : sobre ? Tema.Realce
                    : Parent == null ? Tema.Cartao : Parent.BackColor))
                    e.Graphics.FillPath(pincel, caminho);
                using (var caneta = new Pen(sobre ? Tema.Apagado : Tema.Linha))
                    e.Graphics.DrawPath(caneta, caminho);
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? Tema.Texto : Tema.Fantasma,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>A square icon button: the theme switch, the menu, minimise, close.</summary>
    internal sealed class BotaoIcone : ControlePintado
    {
        private bool sobre;

        public BotaoIcone()
        {
            Size = new Size(30, 30);
            Cursor = Cursors.Hand;
        }

        public Icone Glifo { get; set; }

        /// <summary>Close needs its own hover, because it is the one destructive control up there.</summary>
        public bool Perigoso { get; set; }

        protected override void OnMouseEnter(EventArgs e) { sobre = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sobre = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Color tinta = Enabled ? (sobre ? Tema.Texto : Tema.Apagado) : Tema.Fantasma;
            if (sobre && Enabled)
            {
                Color realce = Perigoso ? Tema.Veu(Tema.Falha, 0.22) : Tema.Realce;
                if (Perigoso)
                    tinta = Tema.Falha;
                using (GraphicsPath caminho = Desenho.Arredondar(new Rectangle(0, 0, Width - 1, Height - 1), 9))
                using (var pincel = new SolidBrush(realce))
                    e.Graphics.FillPath(pincel, caminho);
            }

            Icones.Desenhar(e.Graphics, Glifo, new RectangleF(0, 0, Width, Height).Inflar(-8), tinta, 1.7f);
        }
    }

    internal static class Retangulos
    {
        public static RectangleF Inflar(this RectangleF r, float quanto)
        {
            return RectangleF.Inflate(r, quanto, quanto);
        }
    }

    /// <summary>The status dot. Site: the pulsing bullet on the open source badge.</summary>
    internal sealed class PontoStatus : ControlePintado
    {
        private readonly Timer pulso;
        private double fase;

        public PontoStatus()
        {
            Size = new Size(9, 9);
            pulso = new Timer { Interval = 60 };
            pulso.Tick += (remetente, argumentos) => { fase += 0.055; Invalidate(); };
        }

        public Color Cor { get; set; }

        /// <summary>Only the temporary state breathes; a settled state stays still.</summary>
        public bool Pulsando
        {
            get { return pulso.Enabled; }
            set
            {
                if (pulso.Enabled == value)
                    return;
                pulso.Enabled = value;
                fase = 0;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            double intensidade = Pulsando ? 0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(fase * Math.PI)) : 1.0;
            using (var pincel = new SolidBrush(BotaoPrimario.Mesclar(BackColorEfetivo(), Cor, intensidade)))
                e.Graphics.FillEllipse(pincel, 0, 0, Width - 1, Height - 1);
        }

        private Color BackColorEfetivo()
        {
            return Parent == null ? Tema.Cartao : Parent.BackColor;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                pulso.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The busy indicator, drawn as a single arc so it reads as one stroke.</summary>
    internal sealed class Girando : ControlePintado
    {
        private readonly Timer relogio;
        private int angulo;

        public Girando()
        {
            Size = new Size(18, 18);
            relogio = new Timer { Interval = 33 };
            relogio.Tick += (remetente, argumentos) => { angulo = (angulo + 12) % 360; Invalidate(); };
        }

        public bool Ativo
        {
            get { return relogio.Enabled; }
            set { relogio.Enabled = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var caneta = new Pen(Tema.Apagado, 1.8f))
            {
                caneta.StartCap = LineCap.Round;
                caneta.EndCap = LineCap.Round;
                e.Graphics.DrawArc(caneta, 2, 2, Width - 5, Height - 5, angulo, 260);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                relogio.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The five minute window, drawn as a hairline that empties from the right.
    /// It is the only progress in the program, so it is deliberately thin: it
    /// informs, it does not demand attention.
    /// </summary>
    internal sealed class BarraTempo : ControlePintado
    {
        public BarraTempo() { Height = 3; }

        /// <summary>Between 0 and 1.</summary>
        public double Fracao { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath trilho = Desenho.Arredondar(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (var pincel = new SolidBrush(Tema.Veu(Tema.Temporario, 0.22)))
                e.Graphics.FillPath(pincel, trilho);

            int largura = (int)Math.Round(Math.Max(0, Math.Min(1, Fracao)) * (Width - 1));
            if (largura <= 0)
                return;

            using (GraphicsPath preenchido = Desenho.Arredondar(new Rectangle(0, 0, largura, Height - 1), Height / 2))
            using (var pincel = new SolidBrush(Tema.Temporario))
                e.Graphics.FillPath(pincel, preenchido);
        }
    }

    /// <summary>The window mark, taken from the executable's own icon so it can never drift from it.</summary>
    internal sealed class MarcaApp : ControlePintado
    {
        private static readonly Image Arte = Carregar();

        public MarcaApp() { Size = new Size(24, 24); }

        private static Image Carregar()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); }
            catch { return null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Arte != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(Arte, 0, 0, Width, Height);
                return;
            }

            // The header must never blank, even if the icon cannot be read back.
            using (var pincel = new SolidBrush(Tema.Texto))
                e.Graphics.FillEllipse(pincel, 1, 1, Width - 2, Height - 2);
        }
    }

    internal sealed class CoresMenu : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Tema.Elevado; } }
        public override Color ImageMarginGradientBegin { get { return Tema.Elevado; } }
        public override Color ImageMarginGradientMiddle { get { return Tema.Elevado; } }
        public override Color ImageMarginGradientEnd { get { return Tema.Elevado; } }
        public override Color MenuItemSelected { get { return Tema.RealceForte; } }
        public override Color MenuItemBorder { get { return Tema.RealceForte; } }
        public override Color MenuBorder { get { return Tema.LinhaForte; } }
        public override Color SeparatorDark { get { return Tema.Linha; } }
        public override Color SeparatorLight { get { return Tema.Linha; } }
    }
}
