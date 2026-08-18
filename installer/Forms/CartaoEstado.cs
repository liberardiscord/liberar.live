using Droute.Installer.Classes;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Droute.Installer.Forms
{
    /// <summary>
    /// The card that says where you are and offers the single next move.
    ///
    /// One state, one sentence, one button. Everything that is not that lives
    /// somewhere else in the window, which is what keeps the program readable at a
    /// glance for someone who only wants their camera back.
    /// </summary>
    internal sealed class CartaoEstado : ControlePintado
    {
        private const int Recuo = 20;
        private const int AlturaTitulo = 26;
        private const int AlturaBotao = 44;

        private readonly Font fonteTitulo = Tema.FonteMedia(14f);
        private readonly Font fonteDescricao = Tema.Fonte(9.5f);
        private readonly Font fonteTempo = Tema.Fonte(9f);
        private readonly Font fonteLocal = Tema.FonteMedia(9.5f);
        private readonly Font fonteErro = Tema.Fonte(8.5f);

        private readonly PontoStatus ponto;
        private readonly Girando girando;
        private readonly BarraTempo barra;

        private string titulo = string.Empty;
        private string descricao = string.Empty;
        private Bandeira bandeira;
        private string local;
        private string continuacao;
        private string tempo;
        private string erro;
        private Rectangle areaErro;
        private bool sobreErro;

        public CartaoEstado()
        {
            ponto = new PontoStatus { Cor = Tema.Apagado };
            girando = new Girando { Visible = false };
            barra = new BarraTempo { Visible = false };
            Controls.Add(ponto);
            Controls.Add(girando);
            Controls.Add(barra);

            Botao = new BotaoPrimario { Font = Tema.FonteMedia(10f) };
            Controls.Add(Botao);
        }

        public BotaoPrimario Botao { get; private set; }

        /// <summary>Raised when the failure strip is clicked, which opens the log.</summary>
        public event Action ErroClicado;

        /// <summary>Raised when the card's own height changes, so the window can follow.</summary>
        public event Action Remedido;

        public void Definir(string novoTitulo, string novaDescricao, Color corDoPonto, bool pulsar, bool ocupado)
        {
            titulo = novoTitulo ?? string.Empty;
            descricao = novaDescricao ?? string.Empty;
            local = null;
            continuacao = null;
            ponto.Cor = corDoPonto;
            ponto.Pulsando = pulsar && !ocupado;
            ponto.Visible = !ocupado;
            girando.Visible = ocupado;
            girando.Ativo = ocupado;
            Remedir();
            Invalidate();
        }

        /// <summary>
        /// Names where the connection is coming out, as a badge with the country's
        /// flag, followed by the rest of the sentence.
        ///
        /// It is a badge and not a word inside the paragraph because it is the one
        /// fact on the card that changes what the Discord does, and a reader
        /// skimming the card should land on it without reading the sentence.
        ///
        /// Call it after <see cref="Definir"/>, which clears it.
        /// </summary>
        public void DefinirOrigem(Bandeira qual, string nome, string restante)
        {
            bandeira = qual;
            local = string.IsNullOrEmpty(nome) ? null : nome;
            continuacao = string.IsNullOrEmpty(restante) ? null : restante;
            Remedir();
            Invalidate();
        }

        /// <summary>Shows the countdown row, or hides it when <paramref name="texto"/> is null.</summary>
        public void DefinirTempo(string texto, double fracaoRestante)
        {
            bool mudouPresenca = (texto == null) != (tempo == null);
            tempo = texto;
            barra.Visible = texto != null;
            barra.Fracao = fracaoRestante;
            barra.Invalidate();
            if (mudouPresenca)
                Remedir();
            Invalidate();
        }

        public void DefinirErro(string mensagem)
        {
            if (erro == mensagem)
                return;
            erro = mensagem;
            Remedir();
            Invalidate();
        }

        public void AplicarTema()
        {
            BackColor = Tema.Cartao;
            Botao.Invalidate();
            ponto.Invalidate();
            Invalidate();
        }

        // ------------------------------------------------------------- medidas

        private int LarguraTexto { get { return Math.Max(40, Width - Recuo * 2); } }

        private int TopoDescricao { get { return 14 + AlturaTitulo + 6; } }

        private int AlturaDescricao { get { return Desenho.Altura(descricao, fonteDescricao, LarguraTexto); } }

        private const int AlturaDistintivo = 28;
        private const int LarguraBandeira = 20;
        private const int AlturaBandeira = 14;

        /// <summary>Vertical space the badge and its trailing paragraph take.</summary>
        private int AlturaOrigem
        {
            get
            {
                if (local == null)
                    return 0;
                int altura = 12 + AlturaDistintivo;
                if (continuacao != null)
                    altura += 10 + Desenho.Altura(continuacao, fonteDescricao, LarguraTexto);
                return altura;
            }
        }

        private Rectangle AreaDistintivo
        {
            get
            {
                int largura = 11 + LarguraBandeira + 8
                    + TextRenderer.MeasureText(local, fonteLocal).Width + 13;
                return new Rectangle(Recuo, TopoDescricao + AlturaDescricao + 12, largura, AlturaDistintivo);
            }
        }

        private int AlturaErro
        {
            get
            {
                if (string.IsNullOrEmpty(erro))
                    return 0;
                return Math.Max(34, Desenho.Altura(erro, fonteErro, LarguraTexto - 46) + 18);
            }
        }

        private void Remedir()
        {
            int y = TopoDescricao + AlturaDescricao + AlturaOrigem;
            if (tempo != null)
                y += 18 + 18 + 8;
            if (AlturaErro > 0)
                y += 16 + AlturaErro;
            int altura = y + 18 + AlturaBotao + Recuo;

            bool mudou = Height != altura;
            if (mudou)
                Height = altura;

            Posicionar();
            if (mudou)
            {
                Action remedido = Remedido;
                if (remedido != null)
                    remedido();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            Posicionar();
            base.OnResize(e);
        }

        private void Posicionar()
        {
            ponto.Location = new Point(Recuo, 14 + (AlturaTitulo - ponto.Height) / 2);
            girando.Location = new Point(Recuo - 4, 14 + (AlturaTitulo - girando.Height) / 2);

            int y = TopoDescricao + AlturaDescricao + AlturaOrigem;
            if (tempo != null)
            {
                barra.SetBounds(Recuo, y + 18 + 14, LarguraTexto, 3);
                y += 18 + 18 + 8;
            }

            areaErro = AlturaErro > 0
                ? new Rectangle(Recuo, y + 16, LarguraTexto, AlturaErro)
                : Rectangle.Empty;
            if (AlturaErro > 0)
                y += 16 + AlturaErro;

            Botao.SetBounds(Recuo, y + 18, LarguraTexto, AlturaBotao);
        }

        // ------------------------------------------------------------- interação

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool agora = !areaErro.IsEmpty && areaErro.Contains(e.Location);
            if (agora != sobreErro)
            {
                sobreErro = agora;
                Cursor = agora ? Cursors.Hand : Cursors.Default;
                Invalidate(areaErro);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (sobreErro)
            {
                sobreErro = false;
                Cursor = Cursors.Default;
                Invalidate(areaErro);
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (!areaErro.IsEmpty && areaErro.Contains(e.Location))
            {
                Action clicado = ErroClicado;
                if (clicado != null)
                    clicado();
            }
            base.OnMouseClick(e);
        }

        // ------------------------------------------------------------- pintura

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle area = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath caminho = Desenho.Arredondar(area, 14))
            {
                using (var pincel = new SolidBrush(Tema.Cartao))
                    g.FillPath(pincel, caminho);
                using (var caneta = new Pen(Tema.Linha))
                    g.DrawPath(caneta, caminho);
            }

            TextRenderer.DrawText(g, titulo, fonteTitulo,
                new Rectangle(Recuo + 17, 14, LarguraTexto - 17, AlturaTitulo), Tema.Texto,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, descricao, fonteDescricao,
                new Rectangle(Recuo, TopoDescricao, LarguraTexto, AlturaDescricao), Tema.Apagado, Desenho.Corrido);

            if (local != null)
            {
                Rectangle selo = AreaDistintivo;
                using (GraphicsPath caixa = Desenho.Arredondar(
                    new Rectangle(selo.X, selo.Y, selo.Width - 1, selo.Height - 1), 9))
                {
                    using (var pincel = new SolidBrush(Tema.Realce))
                        g.FillPath(pincel, caixa);
                    using (var caneta = new Pen(Tema.Linha))
                        g.DrawPath(caneta, caixa);
                }

                Icones.DesenharBandeira(g, bandeira, new RectangleF(
                    selo.X + 11, selo.Y + (AlturaDistintivo - AlturaBandeira) / 2f,
                    LarguraBandeira, AlturaBandeira));

                TextRenderer.DrawText(g, local, fonteLocal,
                    new Rectangle(selo.X + 11 + LarguraBandeira + 8, selo.Y, selo.Width, AlturaDistintivo),
                    Tema.Texto, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                if (continuacao != null)
                {
                    int topo = selo.Bottom + 10;
                    TextRenderer.DrawText(g, continuacao, fonteDescricao,
                        new Rectangle(Recuo, topo, LarguraTexto,
                            Desenho.Altura(continuacao, fonteDescricao, LarguraTexto)),
                        Tema.Apagado, Desenho.Corrido);
                }
            }

            if (tempo != null)
            {
                int y = TopoDescricao + AlturaDescricao + AlturaOrigem + 18;
                Icones.Desenhar(g, Icone.Relogio, new RectangleF(Recuo, y, 13, 13), Tema.Temporario, 1.9f);
                TextRenderer.DrawText(g, tempo, fonteTempo,
                    new Rectangle(Recuo + 19, y - 2, LarguraTexto - 19, 17), Tema.Temporario,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            if (areaErro.IsEmpty)
                return;

            using (GraphicsPath caixa = Desenho.Arredondar(
                new Rectangle(areaErro.X, areaErro.Y, areaErro.Width - 1, areaErro.Height - 1), 10))
            using (var pincel = new SolidBrush(Tema.Veu(Tema.Falha, sobreErro ? 0.20 : 0.13)))
                g.FillPath(pincel, caixa);

            Icones.Desenhar(g, Icone.Alerta, new RectangleF(areaErro.X + 13, areaErro.Y + 10, 14, 14), Tema.Falha, 1.8f);
            int alturaTexto = Desenho.Altura(erro, fonteErro, areaErro.Width - 46);
            TextRenderer.DrawText(g, erro, fonteErro,
                new Rectangle(areaErro.X + 35, areaErro.Y + (areaErro.Height - alturaTexto) / 2, areaErro.Width - 46, alturaTexto),
                Tema.Falha, Desenho.Corrido);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fonteTitulo.Dispose();
                fonteDescricao.Dispose();
                fonteTempo.Dispose();
                fonteLocal.Dispose();
                fonteErro.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
