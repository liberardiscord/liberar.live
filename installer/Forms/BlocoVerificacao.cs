using Droute.Installer.Classes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Droute.Installer.Forms
{
    /// <summary>
    /// Answers one question, which is the question people actually open this
    /// program with: is everything in place?
    ///
    /// When it is, that is a single calm line and nothing else. When it is not,
    /// the block opens itself and says which line failed and what to do about it,
    /// because a checklist that hides its own failure is worse than no checklist.
    /// </summary>
    internal sealed class BlocoVerificacao : ControlePintado
    {
        private const int AlturaResumo = 38;
        private const int RecuoIcone = 18;
        private const int RecuoTexto = 42;
        private const int RecuoItem = 26;
        private const int RecuoItemTexto = 50;

        private readonly Font fonteResumo = Tema.Fonte(9.5f);
        private readonly Font fonteItem = Tema.Fonte(9f);
        private readonly Font fonteDetalhe = Tema.Fonte(8.5f);

        private List<ItemVerificacao> itens = new List<ItemVerificacao>();
        private bool aberto;
        private bool sobreResumo;

        public BlocoVerificacao()
        {
            Cursor = Cursors.Hand;
            Height = AlturaResumo;
        }

        /// <summary>Raised when the block grows or shrinks, so the window can follow.</summary>
        public event Action Remedido;

        public void Atualizar(List<ItemVerificacao> novos)
        {
            itens = novos ?? new List<ItemVerificacao>();

            // A failure opens the block on its own. Once open it stays open, so the
            // list does not collapse under someone mid-read the moment it is fixed.
            if (Verificacao.PrimeiraFalha(itens) != null)
                aberto = true;

            Remedir();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { sobreResumo = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sobreResumo = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool agora = e.Y < AlturaResumo;
            if (agora != sobreResumo)
            {
                sobreResumo = agora;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Y < AlturaResumo)
            {
                aberto = !aberto;
                Remedir();
                Invalidate();
            }
            base.OnMouseClick(e);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            Remedir();
            base.OnFontChanged(e);
        }

        private void Remedir()
        {
            int altura = AlturaResumo;
            if (aberto)
            {
                altura += 4;
                foreach (ItemVerificacao item in itens)
                    altura += AlturaDoItem(item);
                altura += 8;
            }

            if (Height == altura)
                return;

            Height = altura;
            Action remedido = Remedido;
            if (remedido != null)
                remedido();
        }

        private int AlturaDoItem(ItemVerificacao item)
        {
            int largura = Math.Max(40, Width - RecuoItemTexto - 20);
            int altura = Math.Max(22, Desenho.Altura(item.TituloAtual, fonteItem, largura) + 6);
            if (item.Situacao == Situacao.Falhou && !string.IsNullOrEmpty(item.Detalhe))
                altura += Desenho.Altura(item.Detalhe, fonteDetalhe, largura) + 4;
            return altura;
        }

        protected override void OnResize(EventArgs e)
        {
            Remedir();
            base.OnResize(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // A surface of its own, not text loose on the window. Without it this
            // block and the advertising strip below share a background, which
            // groups them together, and they have nothing to do with each other.
            using (GraphicsPath fundo = Desenho.Arredondar(new Rectangle(0, 0, Width - 1, Height - 1), 14))
            using (var pincelFundo = new SolidBrush(Tema.Cartao))
                g.FillPath(pincelFundo, fundo);

            if (sobreResumo)
            {
                using (GraphicsPath realce = Desenho.Arredondar(new Rectangle(0, 0, Width - 1, AlturaResumo - 1), 10))
                using (var pincel = new SolidBrush(Tema.Realce))
                    g.FillPath(pincel, realce);
            }

            int falhas = 0;
            bool conferindo = false;
            foreach (ItemVerificacao item in itens)
            {
                if (item.Situacao == Situacao.Falhou)
                    falhas++;
                if (item.Situacao == Situacao.Verificando)
                    conferindo = true;
            }

            Icone glifo;
            Color tinta;
            string resumo;
            if (falhas > 0)
            {
                glifo = Icone.Alerta;
                tinta = Tema.Falha;
                resumo = falhas == 1 ? "falta 1 coisa para transmitir" : "faltam " + falhas + " coisas para transmitir";
            }
            else if (conferindo)
            {
                glifo = Icone.Relogio;
                tinta = Tema.Apagado;
                resumo = "conferindo se está tudo certo";
            }
            else
            {
                glifo = Icone.Check;
                tinta = Tema.Pronto;
                resumo = "tudo certo para transmitir";
            }

            Icones.Desenhar(g, glifo, new RectangleF(RecuoIcone, (AlturaResumo - 15) / 2f, 15, 15), tinta, 2f);
            TextRenderer.DrawText(g, resumo, fonteResumo,
                new Rectangle(RecuoTexto, 0, Width - RecuoTexto - 40, AlturaResumo),
                falhas > 0 ? Tema.Texto : Tema.Apagado,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            Icones.Desenhar(g, aberto ? Icone.ChevronCima : Icone.ChevronBaixo,
                new RectangleF(Width - 32, (AlturaResumo - 14) / 2f, 14, 14), Tema.Fantasma, 1.7f);

            if (!aberto)
                return;

            int y = AlturaResumo + 4;
            int largura = Math.Max(40, Width - RecuoItemTexto - 20);
            foreach (ItemVerificacao item in itens)
            {
                Icone marca;
                Color cor;
                switch (item.Situacao)
                {
                    case Situacao.Ok: marca = Icone.Check; cor = Tema.Pronto; break;
                    case Situacao.Falhou: marca = Icone.Cruz; cor = Tema.Falha; break;
                    case Situacao.Verificando: marca = Icone.Relogio; cor = Tema.Apagado; break;
                    default: marca = Icone.Relogio; cor = Tema.Fantasma; break;
                }

                int alturaTitulo = Math.Max(22, Desenho.Altura(item.TituloAtual, fonteItem, largura) + 6);
                Icones.Desenhar(g, marca, new RectangleF(RecuoItem, y + (alturaTitulo - 13) / 2f, 13, 13), cor, 2f);
                TextRenderer.DrawText(g, item.TituloAtual, fonteItem,
                    new Rectangle(RecuoItemTexto, y, largura, alturaTitulo),
                    item.Situacao == Situacao.Aguardando ? Tema.Fantasma : Tema.Apagado,
                    Desenho.Corrido | TextFormatFlags.VerticalCenter);
                y += alturaTitulo;

                if (item.Situacao != Situacao.Falhou || string.IsNullOrEmpty(item.Detalhe))
                    continue;

                int alturaDetalhe = Desenho.Altura(item.Detalhe, fonteDetalhe, largura);
                TextRenderer.DrawText(g, item.Detalhe, fonteDetalhe,
                    new Rectangle(RecuoItemTexto, y, largura, alturaDetalhe), Tema.Fantasma, Desenho.Corrido);
                y += alturaDetalhe + 4;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fonteResumo.Dispose();
                fonteItem.Dispose();
                fonteDetalhe.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
