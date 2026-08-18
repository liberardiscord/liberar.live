using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Droute.Installer.Classes
{
    /// <summary>The country flags the interface can show in a badge.</summary>
    internal enum Bandeira
    {
        Brasil,
        EstadosUnidos
    }

    internal enum Icone
    {
        Sol,
        Lua,
        Menu,
        Minimizar,
        Fechar,
        Check,
        Cruz,
        Alerta,
        ChevronBaixo,
        ChevronCima,
        Video,
        VideoCortado,
        Baixar,
        LinkExterno,
        Relogio
    }

    /// <summary>
    /// The same drawing language the landing page uses: a 24 unit grid, one
    /// stroke weight, round caps and round joins, no fills. Reproducing the SVGs
    /// here rather than shipping bitmaps keeps every glyph sharp at any DPI and
    /// lets a single colour follow the theme.
    /// </summary>
    internal static class Icones
    {
        /// <summary>
        /// Strokes <paramref name="icone"/> centred in <paramref name="area"/>.
        /// </summary>
        /// <param name="espessura">Stroke width in grid units, as in the SVG markup.</param>
        public static void Desenhar(Graphics g, Icone icone, RectangleF area, Color cor, float espessura = 1.7f)
        {
            SmoothingMode anterior = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float lado = Math.Min(area.Width, area.Height);
            float escala = lado / 24f;
            GraphicsState estado = g.Save();
            g.TranslateTransform(area.X + (area.Width - lado) / 2f, area.Y + (area.Height - lado) / 2f);
            g.ScaleTransform(escala, escala);

            using (var caneta = new Pen(cor, espessura))
            {
                caneta.StartCap = LineCap.Round;
                caneta.EndCap = LineCap.Round;
                caneta.LineJoin = LineJoin.Round;
                Tracar(g, caneta, icone, cor);
            }

            g.Restore(estado);
            g.SmoothingMode = anterior;
        }

        private static void Tracar(Graphics g, Pen caneta, Icone icone, Color cor)
        {
            switch (icone)
            {
                case Icone.Sol:
                    g.DrawEllipse(caneta, 8f, 8f, 8f, 8f);
                    g.DrawLine(caneta, 12f, 2f, 12f, 4f);
                    g.DrawLine(caneta, 12f, 20f, 12f, 22f);
                    g.DrawLine(caneta, 4.9f, 4.9f, 6.3f, 6.3f);
                    g.DrawLine(caneta, 17.7f, 17.7f, 19.1f, 19.1f);
                    g.DrawLine(caneta, 2f, 12f, 4f, 12f);
                    g.DrawLine(caneta, 20f, 12f, 22f, 12f);
                    g.DrawLine(caneta, 4.9f, 19.1f, 6.3f, 17.7f);
                    g.DrawLine(caneta, 17.7f, 6.3f, 19.1f, 4.9f);
                    break;

                case Icone.Lua:
                    // The site's crescent, expressed as the two circular arcs the
                    // SVG path describes: outer r9 centred near the grid centre,
                    // closed by a smaller r7 arc that bites the top right away.
                    g.DrawArc(caneta, 3.04f, 2.96f, 18f, 18f, 5.4f, 259.2f);
                    g.DrawArc(caneta, 9.8f, 0.2f, 14f, 14f, 216.9f, -163.8f);
                    break;

                case Icone.Menu:
                    g.DrawLine(caneta, 4f, 7f, 20f, 7f);
                    g.DrawLine(caneta, 4f, 12f, 20f, 12f);
                    g.DrawLine(caneta, 4f, 17f, 20f, 17f);
                    break;

                case Icone.Minimizar:
                    g.DrawLine(caneta, 5f, 12f, 19f, 12f);
                    break;

                case Icone.Fechar:
                case Icone.Cruz:
                    g.DrawLine(caneta, 6f, 6f, 18f, 18f);
                    g.DrawLine(caneta, 18f, 6f, 6f, 18f);
                    break;

                case Icone.Check:
                    g.DrawLines(caneta, new[] { new PointF(20f, 6f), new PointF(9f, 17f), new PointF(4f, 12f) });
                    break;

                case Icone.Alerta:
                    g.DrawEllipse(caneta, 2f, 2f, 20f, 20f);
                    g.DrawLine(caneta, 12f, 7.5f, 12f, 13f);
                    using (var ponto = new SolidBrush(cor))
                        g.FillEllipse(ponto, 10.9f, 15.4f, 2.2f, 2.2f);
                    break;

                case Icone.ChevronBaixo:
                    g.DrawLines(caneta, new[] { new PointF(6f, 9.5f), new PointF(12f, 15.5f), new PointF(18f, 9.5f) });
                    break;

                case Icone.ChevronCima:
                    g.DrawLines(caneta, new[] { new PointF(6f, 14.5f), new PointF(12f, 8.5f), new PointF(18f, 14.5f) });
                    break;

                case Icone.Video:
                    DesenharCamera(g, caneta);
                    break;

                case Icone.VideoCortado:
                    // Without knowing what is behind the glyph there is no gap to
                    // punch, so the slash simply crosses the body. Callers that can
                    // name the surface should use DesenharVideoCortado instead.
                    DesenharCamera(g, caneta);
                    g.DrawLine(caneta, 3f, 3f, 21f, 21f);
                    break;

                case Icone.Baixar:
                    g.DrawLines(caneta, new[]
                    {
                        new PointF(3.5f, 15f), new PointF(3.5f, 19f),
                        new PointF(20.5f, 19f), new PointF(20.5f, 15f)
                    });
                    g.DrawLine(caneta, 12f, 3.5f, 12f, 15f);
                    g.DrawLines(caneta, new[] { new PointF(7f, 10f), new PointF(12f, 15f), new PointF(17f, 10f) });
                    break;

                case Icone.LinkExterno:
                    g.DrawLine(caneta, 7f, 17f, 17f, 7f);
                    g.DrawLines(caneta, new[] { new PointF(7.5f, 7f), new PointF(17f, 7f), new PointF(17f, 16.5f) });
                    break;

                case Icone.Relogio:
                    g.DrawEllipse(caneta, 2.5f, 2.5f, 19f, 19f);
                    g.DrawLines(caneta, new[] { new PointF(12f, 6.5f), new PointF(12f, 12f), new PointF(16f, 14f) });
                    break;
            }
        }

        /// <summary>
        /// Strokes the camera with a slash through it, punching a gap in the body
        /// so the two shapes never touch. <paramref name="vao"/> is whatever the
        /// icon sits on, which is what the gap is filled with.
        /// </summary>
        public static void DesenharVideoCortado(Graphics g, RectangleF area, Color cor, Color vao, float espessura = 1.7f)
        {
            SmoothingMode anterior = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float lado = Math.Min(area.Width, area.Height);
            float escala = lado / 24f;
            GraphicsState estado = g.Save();
            g.TranslateTransform(area.X + (area.Width - lado) / 2f, area.Y + (area.Height - lado) / 2f);
            g.ScaleTransform(escala, escala);

            using (var caneta = new Pen(cor, espessura))
            using (var recorte = new Pen(vao, espessura * 3.4f))
            {
                caneta.StartCap = caneta.EndCap = LineCap.Round;
                caneta.LineJoin = LineJoin.Round;
                recorte.StartCap = recorte.EndCap = LineCap.Round;

                DesenharCamera(g, caneta);
                g.DrawLine(recorte, 3f, 3f, 21f, 21f);
                g.DrawLine(caneta, 3f, 3f, 21f, 21f);
            }

            g.Restore(estado);
            g.SmoothingMode = anterior;
        }

        private static void DesenharCamera(Graphics g, Pen caneta)
        {
            using (GraphicsPath corpo = Arredondado(new RectangleF(2f, 6f, 14f, 12f), 2.5f))
                g.DrawPath(caneta, corpo);
            g.DrawLines(caneta, new[]
            {
                new PointF(22f, 8f), new PointF(16f, 12f), new PointF(22f, 16f), new PointF(22f, 8f)
            });
        }

        private static GraphicsPath Arredondado(RectangleF r, float raio)
        {
            float d = raio * 2f;
            var caminho = new GraphicsPath();
            caminho.AddArc(r.Left, r.Top, d, d, 180f, 90f);
            caminho.AddArc(r.Right - d, r.Top, d, d, 270f, 90f);
            caminho.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            caminho.AddArc(r.Left, r.Bottom - d, d, d, 90f, 90f);
            caminho.CloseFigure();
            return caminho;
        }

        /// <summary>
        /// The Brazilian flag, drawn rather than typed.
        ///
        /// Windows ships no glyphs for regional indicator pairs, so the emoji
        /// renders as the letters "BR" in two boxes. At badge size the banner and
        /// its motto would be illegible anyway, so the shape stops at the parts
        /// that carry the recognition: field, rhombus, globe and band.
        /// </summary>
        public static void DesenharBandeiraBrasil(Graphics g, RectangleF area)
        {
            SmoothingMode antes = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var verde = Color.FromArgb(0x00, 0x9B, 0x3A);
            var amarelo = Color.FromArgb(0xFE, 0xDF, 0x00);
            var azul = Color.FromArgb(0x00, 0x27, 0x76);

            float l = area.Width, a = area.Height;
            float raioCanto = Math.Min(3f, Math.Min(l, a) * 0.18f);

            using (var campo = CaminhoArredondado(area, raioCanto))
            using (var pincel = new SolidBrush(verde))
                g.FillPath(pincel, campo);

            using (var losango = new GraphicsPath())
            {
                losango.AddPolygon(new[]
                {
                    new PointF(area.X + l * 0.50f, area.Y + a * 0.10f),
                    new PointF(area.X + l * 0.91f, area.Y + a * 0.50f),
                    new PointF(area.X + l * 0.50f, area.Y + a * 0.90f),
                    new PointF(area.X + l * 0.09f, area.Y + a * 0.50f)
                });
                using (var pincel = new SolidBrush(amarelo))
                    g.FillPath(pincel, losango);
            }

            float raio = a * 0.25f;
            float cx = area.X + l * 0.5f;
            float cy = area.Y + a * 0.5f;
            var globo = new RectangleF(cx - raio, cy - raio, raio * 2f, raio * 2f);
            using (var pincel = new SolidBrush(azul))
                g.FillEllipse(pincel, globo);

            // The band only exists inside the globe, so it is clipped to it
            // instead of being fitted by hand at every size.
            GraphicsState estado = g.Save();
            using (var recorte = new GraphicsPath())
            {
                recorte.AddEllipse(globo);
                g.SetClip(recorte, CombineMode.Intersect);
                using (var caneta = new Pen(Color.White, Math.Max(0.9f, raio * 0.34f)))
                    g.DrawArc(caneta, cx - raio * 1.9f, cy - raio * 0.15f, raio * 3.8f, raio * 3.8f, 205f, 130f);
            }
            g.Restore(estado);

            g.SmoothingMode = antes;
        }

        public static void DesenharBandeira(Graphics g, Bandeira qual, RectangleF area)
        {
            if (qual == Bandeira.EstadosUnidos)
                DesenharBandeiraEUA(g, area);
            else
                DesenharBandeiraBrasil(g, area);
        }

        /// <summary>
        /// The flag of the United States, at a size where its own rules stop being
        /// drawable.
        ///
        /// Thirteen stripes across fourteen pixels puts each one below a pixel, and
        /// fifty stars puts each one below a fifth of one. The stripes are painted
        /// anyway, because their rhythm is the whole recognition; the stars become a
        /// texture, because a texture is what the eye sees there at this size.
        /// </summary>
        public static void DesenharBandeiraEUA(Graphics g, RectangleF area)
        {
            SmoothingMode antes = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var vermelho = Color.FromArgb(0xB2, 0x22, 0x34);
            var azul = Color.FromArgb(0x3C, 0x3B, 0x6E);
            float raioCanto = Math.Min(3f, Math.Min(area.Width, area.Height) * 0.18f);

            GraphicsState estado = g.Save();
            using (var campo = CaminhoArredondado(area, raioCanto))
            {
                using (var pincel = new SolidBrush(Color.White))
                    g.FillPath(pincel, campo);

                // Painted inside the rounded field instead of trimmed to it after,
                // so a sub-pixel stripe never bleeds past the corner.
                g.SetClip(campo, CombineMode.Intersect);

                float faixa = area.Height / 13f;
                using (var pincel = new SolidBrush(vermelho))
                {
                    for (int i = 0; i < 13; i += 2)
                        g.FillRectangle(pincel, area.X, area.Y + i * faixa, area.Width, faixa + 0.15f);
                }

                var canton = new RectangleF(area.X, area.Y, area.Width * 0.42f, faixa * 7f);
                using (var pincel = new SolidBrush(azul))
                    g.FillRectangle(pincel, canton);

                using (var pincel = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                {
                    float passoX = canton.Width / 4.4f;
                    float passoY = canton.Height / 3.4f;
                    float ponto = Math.Max(0.62f, canton.Height * 0.085f);
                    for (int linha = 0; linha < 3; linha++)
                    {
                        for (int coluna = 0; coluna < 4; coluna++)
                        {
                            float x = canton.X + passoX * (coluna + 0.55f)
                                + (linha % 2 == 1 ? passoX * 0.4f : 0f);
                            float y = canton.Y + passoY * (linha + 0.72f);
                            if (x + ponto > canton.Right)
                                continue;
                            g.FillEllipse(pincel, x - ponto, y - ponto, ponto * 2f, ponto * 2f);
                        }
                    }
                }
            }
            g.Restore(estado);

            g.SmoothingMode = antes;
        }

        private static GraphicsPath CaminhoArredondado(RectangleF area, float raio)
        {
            var caminho = new GraphicsPath();
            float d = raio * 2f;
            if (d <= 0f)
            {
                caminho.AddRectangle(area);
                return caminho;
            }
            caminho.AddArc(area.X, area.Y, d, d, 180, 90);
            caminho.AddArc(area.Right - d, area.Y, d, d, 270, 90);
            caminho.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
            caminho.AddArc(area.X, area.Bottom - d, d, d, 90, 90);
            caminho.CloseFigure();
            return caminho;
        }
    }
}
