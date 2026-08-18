using System.Collections.Generic;

namespace Droute.Installer.Classes
{
    internal enum Situacao
    {
        /// <summary>Checked and fine.</summary>
        Ok,
        /// <summary>Checked and needs the user.</summary>
        Falhou,
        /// <summary>Being checked right now.</summary>
        Verificando,
        /// <summary>Cannot be checked yet, because something before it failed.</summary>
        Aguardando
    }

    /// <summary>Whose side is down when the server does not answer.</summary>
    internal enum AlcanceServidor
    {
        /// <summary>The server answered.</summary>
        Ok,
        /// <summary>Windows reports this machine has no internet connection.</summary>
        SemInternet,
        /// <summary>The connection works, our server is the one that did not answer.</summary>
        ServidorFora
    }

    internal sealed class ItemVerificacao
    {
        public ItemVerificacao(string titulo, string tituloFalha, Situacao situacao, string detalhe)
        {
            Titulo = titulo;
            TituloFalha = tituloFalha;
            Situacao = situacao;
            Detalhe = detalhe;
        }

        /// <summary>How the line reads when it is fine, or not decided yet.</summary>
        public string Titulo { get; private set; }

        /// <summary>
        /// How the line reads when it failed.
        ///
        /// A red cross next to "servidor respondendo" says the opposite of itself:
        /// the mark negates a sentence that is already written as a fact, and the
        /// reader has to do that arithmetic. The line states what is true instead.
        /// </summary>
        public string TituloFalha { get; private set; }

        public Situacao Situacao { get; private set; }

        /// <summary>What to do about it, shown only when the item is not fine.</summary>
        public string Detalhe { get; private set; }

        /// <summary>The wording that matches the current situation.</summary>
        public string TituloAtual
        {
            get { return Situacao == Situacao.Falhou && !string.IsNullOrEmpty(TituloFalha) ? TituloFalha : Titulo; }
        }
    }

    /// <summary>
    /// Turns the scattered install checks into one ordered list the interface can
    /// show as it is.
    ///
    /// The order matters: each line depends on the one above it, so a machine
    /// without Discord shows one real failure and three honest "not yet" lines
    /// instead of four alarming ones. Wording stays in the user's terms, never in
    /// the terms of what the program actually does to the files.
    /// </summary>
    internal static class Verificacao
    {
        public static List<ItemVerificacao> Montar(
            int encontradas, int instaladas, int atuais, Situacao servidor, AlcanceServidor alcance)
        {
            var itens = new List<ItemVerificacao>(4);

            bool temDiscord = encontradas > 0;
            itens.Add(new ItemVerificacao(
                "discord encontrado",
                "discord não encontrado",
                temDiscord ? Situacao.Ok : Situacao.Falhou,
                "instale o discord, abra ele uma vez e volte aqui."));

            bool completo = temDiscord && instaladas >= encontradas;
            itens.Add(new ItemVerificacao(
                encontradas > 1 ? "instalado nas " + encontradas + " versões do discord" : "instalado no discord",
                instaladas > 0 ? "instalação incompleta no discord" : "não instalado no discord",
                !temDiscord ? Situacao.Aguardando : completo ? Situacao.Ok : Situacao.Falhou,
                instaladas > 0
                    ? "uma das versões do discord ficou de fora. clique em instalar."
                    : "clique no botão acima e espere o discord abrir sozinho."));

            itens.Add(new ItemVerificacao(
                "instalação em dia",
                "instalação desatualizada",
                !completo ? Situacao.Aguardando : atuais >= encontradas ? Situacao.Ok : Situacao.Falhou,
                "saiu uma versão nova. clique em atualizar."));

            // The two failures need different words because they need different
            // actions. Telling someone to check a connection that is already fine
            // sends them to restart a router while the problem sits on our side.
            bool semInternet = alcance == AlcanceServidor.SemInternet;
            itens.Add(new ItemVerificacao(
                "servidor respondendo",
                semInternet ? "computador sem internet" : "servidor não respondendo",
                servidor,
                semInternet
                    ? "este computador está sem internet agora. confira a sua conexão e tente de novo."
                    : "a sua internet está funcionando, quem não respondeu foi o nosso servidor, costuma voltar sozinho, tente de novo daqui a pouco."));

            return itens;
        }

        /// <summary>True when every line is fine, which is the only state the program calls ready.</summary>
        public static bool TudoCerto(List<ItemVerificacao> itens)
        {
            foreach (ItemVerificacao item in itens)
            {
                if (item.Situacao != Situacao.Ok)
                    return false;
            }
            return true;
        }

        /// <summary>The first line that needs the user, or null when nothing does.</summary>
        public static ItemVerificacao PrimeiraFalha(List<ItemVerificacao> itens)
        {
            foreach (ItemVerificacao item in itens)
            {
                if (item.Situacao == Situacao.Falhou)
                    return item;
            }
            return null;
        }
    }
}
