using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Text;

namespace Droute.Installer.Classes
{
    /// <summary>
    /// The one place that decides what anything looks like.
    ///
    /// Every value here is the landing page's own token, flattened against the
    /// page background: the site composites translucent surfaces over the body
    /// colour, and Windows Forms cannot, so the same result is precomputed. Keep
    /// the two in step, otherwise the program and the site stop looking like one
    /// product.
    /// </summary>
    internal static class Tema
    {
        private const string ChavePreferencias = @"Software\liberar.live";
        private const string ValorTema = "tema";

        /// <summary>Raised after <see cref="Escuro"/> changes, so open windows can repaint.</summary>
        public static event Action Mudou;

        private static bool _escuro = LerPreferencia();

        public static bool Escuro
        {
            get { return _escuro; }
            set
            {
                if (_escuro == value)
                    return;
                _escuro = value;
                GravarPreferencia(value);
                Action mudou = Mudou;
                if (mudou != null)
                    mudou();
            }
        }

        public static void Alternar() { Escuro = !Escuro; }

        // ------------------------------------------------------------- superfícies

        /// <summary>Page background. Site: --bg.</summary>
        public static Color Fundo
        {
            get { return _escuro ? Cor(0x0D0D0D) : Cor(0xF6F5F2); }
        }

        /// <summary>Panel fill. Site: --card, over --bg.</summary>
        public static Color Cartao
        {
            get { return _escuro ? Cor(0x171717) : Cor(0xEFEEEB); }
        }

        /// <summary>
        /// Anything that floats above the window: dialogs and the menu.
        ///
        /// It is one step past <see cref="Cartao"/> on purpose. A dialog lands on
        /// top of whatever the window was already showing, so matching the surface
        /// underneath it makes the two read as one flat sheet.
        /// </summary>
        public static Color Elevado
        {
            get { return _escuro ? Cor(0x1F1F1F) : Cor(0xFFFFFF); }
        }

        /// <summary>
        /// The strip at the top of a dialog, recessed below <see cref="Elevado"/>.
        ///
        /// A borderless window has nothing that reads as "grab here". The band
        /// gives the eye a title bar to aim at, and it darkens rather than lightens
        /// because a raised strip would compete with the dialog's own content.
        /// </summary>
        public static Color Cabecalho
        {
            get { return _escuro ? Cor(0x111111) : Cor(0xEDECE8); }
        }

        /// <summary>
        /// The edge of a floating surface, stronger than <see cref="Linha"/>.
        ///
        /// A hairline is enough to separate two panels that share a background.
        /// A dialog has to separate itself from content it does not control, and
        /// the quiet line disappears against half of it.
        /// </summary>
        public static Color LinhaForte
        {
            get { return _escuro ? Cor(0x3D3D3D) : Cor(0xC6C4BE); }
        }

        /// <summary>Hairline that gives a panel its edge. Site: --line.</summary>
        public static Color Linha
        {
            get { return _escuro ? Cor(0x2A2A2A) : Cor(0xDCDBD7); }
        }

        /// <summary>Hover wash on a quiet control. Site: --hover.</summary>
        public static Color Realce
        {
            get { return _escuro ? Cor(0x232323) : Cor(0xE9E8E4); }
        }

        /// <summary>Pressed wash, one step past <see cref="Realce"/>.</summary>
        public static Color RealceForte
        {
            get { return _escuro ? Cor(0x303030) : Cor(0xDCDBD7); }
        }

        // ------------------------------------------------------------- tipografia

        /// <summary>Primary text. Site: --fg.</summary>
        public static Color Texto
        {
            get { return _escuro ? Cor(0xE8E6E1) : Cor(0x17161A); }
        }

        /// <summary>Secondary text. Site: --muted.</summary>
        public static Color Apagado
        {
            get { return _escuro ? Cor(0x7C7A75) : Cor(0x6D6B66); }
        }

        /// <summary>Text that must stay readable but recede further than <see cref="Apagado"/>.</summary>
        public static Color Fantasma
        {
            get { return _escuro ? Cor(0x5A5955) : Cor(0x96948E); }
        }

        // ------------------------------------------------------------- ação

        /// <summary>Primary button fill. Site: --btn-bg.</summary>
        public static Color BotaoFundo
        {
            get { return _escuro ? Cor(0xE8E6E1) : Cor(0x17161A); }
        }

        /// <summary>Primary button label. Site: --btn-fg.</summary>
        public static Color BotaoTexto
        {
            get { return _escuro ? Cor(0x0A0A0A) : Cor(0xF6F5F2); }
        }

        // ------------------------------------------------------------- estado
        //
        // The interface is monochrome on purpose; colour is reserved for the few
        // places where it carries meaning, and never used decoratively. Each tone
        // is desaturated to sit next to the warm off-white without shouting.

        /// <summary>Everything checked out.</summary>
        public static Color Pronto
        {
            get { return _escuro ? Cor(0x62B37E) : Cor(0x2F7D4F); }
        }

        /// <summary>Temporary state that will end on its own.</summary>
        public static Color Temporario
        {
            get { return _escuro ? Cor(0xD9A441) : Cor(0x9A6B12); }
        }

        /// <summary>Something needs the user.</summary>
        public static Color Falha
        {
            get { return _escuro ? Cor(0xD96A63) : Cor(0xA83A32); }
        }

        /// <summary>Very low alpha wash of <paramref name="cor"/>, flattened over the card.</summary>
        public static Color Veu(Color cor, double alfa)
        {
            Color baseCor = Cartao;
            return Color.FromArgb(
                (int)Math.Round(baseCor.R + (cor.R - baseCor.R) * alfa),
                (int)Math.Round(baseCor.G + (cor.G - baseCor.G) * alfa),
                (int)Math.Round(baseCor.B + (cor.B - baseCor.B) * alfa));
        }

        // ------------------------------------------------------------- fontes
        //
        // The site is set in Inter. Almost no Windows machine has it, so Segoe UI
        // Variable and then Segoe UI stand in: same humanist grotesque skeleton,
        // same tight tracking at display sizes, no layout surprises.

        private static readonly string Familia = EscolherFamilia();

        public static Font Fonte(float tamanho)
        {
            return new Font(Familia, tamanho, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font Fonte(float tamanho, FontStyle estilo)
        {
            return new Font(Familia, tamanho, estilo, GraphicsUnit.Point);
        }

        /// <summary>
        /// The weight titles are set in. The site uses 500 to 600, which is a step
        /// below bold; Windows exposes that as its own family rather than a style,
        /// so when it exists it is used, and bold only stands in when it does not.
        /// </summary>
        public static Font FonteMedia(float tamanho)
        {
            return FamiliaMedia == null
                ? new Font(Familia, tamanho, FontStyle.Bold, GraphicsUnit.Point)
                : new Font(FamiliaMedia, tamanho, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static readonly string FamiliaMedia = EscolherMedia();

        private static string EscolherMedia()
        {
            string[] preferidas = { Familia + " SemiBold", Familia + " Semibold", "Segoe UI Semibold" };
            try
            {
                using (var instaladas = new InstalledFontCollection())
                {
                    foreach (string nome in preferidas)
                    {
                        foreach (FontFamily familia in instaladas.Families)
                        {
                            if (string.Equals(familia.Name, nome, StringComparison.OrdinalIgnoreCase))
                                return familia.Name;
                        }
                    }
                }
            }
            catch
            {
                // Falling back to bold is a visual compromise, never a failure.
            }
            return null;
        }

        private static string EscolherFamilia()
        {
            string[] preferidas = { "Inter", "Inter Display", "Segoe UI Variable Text", "Segoe UI" };
            try
            {
                using (var instaladas = new InstalledFontCollection())
                {
                    foreach (string nome in preferidas)
                    {
                        foreach (FontFamily familia in instaladas.Families)
                        {
                            if (string.Equals(familia.Name, nome, StringComparison.OrdinalIgnoreCase))
                                return familia.Name;
                        }
                    }
                }
            }
            catch
            {
                // Enumeration can fail on locked-down machines; Segoe UI always exists.
            }
            return "Segoe UI";
        }

        // ------------------------------------------------------------- persistência

        private static bool LerPreferencia()
        {
            try
            {
                using (RegistryKey chave = Registry.CurrentUser.OpenSubKey(ChavePreferencias, false))
                {
                    // Dark is the default, matching the site, which opens dark
                    // regardless of what the system prefers.
                    object valor = chave == null ? null : chave.GetValue(ValorTema);
                    return !string.Equals(valor as string, "claro", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return true;
            }
        }

        private static void GravarPreferencia(bool escuro)
        {
            try
            {
                using (RegistryKey chave = Registry.CurrentUser.CreateSubKey(ChavePreferencias))
                    chave.SetValue(ValorTema, escuro ? "escuro" : "claro", RegistryValueKind.String);
            }
            catch
            {
                // A preference that cannot be saved is not worth interrupting anyone over.
            }
        }

        private static Color Cor(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }
}
