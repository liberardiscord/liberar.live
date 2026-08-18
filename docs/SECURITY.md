# segurança e privacidade

## escopo do tráfego

o projeto não habilita proxy do sistema, não cria adaptador de rede e não instala serviço. os hooks são carregados dentro do Discord e afetam somente sockets criados nesses processos.

isso não torna o proxy invisível: o operador do servidor vê conexões, horários, volume e destinos encaminhados. o conteúdo protegido por TLS continua criptografado entre o Discord e os serviços de destino, mas metadados de rede permanecem observáveis.

## credenciais

**não existe credencial compilada no binário.** nenhum usuário, nenhuma senha, nem em `proxy_config.example.hpp` nem em `proxy_config.local.hpp`. o único valor de rede que sobra é um endpoint de reserva para builds privados, que não é segredo.

o motivo é simples e não tem contorno: segredo embutido em binário distribuído sempre é recuperável, seja por `strings`, por engenharia reversa de `droute.dll` ou a partir do payload dentro de `liberar.live.exe`. a saída não é esconder melhor, é fazer o valor extraído não servir para nada.

o modelo é o mesmo do cliente do Mullvad, que também não carrega segredo algum: a autoridade vem de uma chave que existe só na máquina do usuário.

### como funciona

1. na primeira ativação o cliente gera um par ECDSA P-256 local. a chave privada é gravada em `%LOCALAPPDATA%\liberar.live\device.key`, selada com DPAPI no escopo do usuário, e nunca sai da máquina.
2. o dispositivo se registra no broker pagando um proof-of-work, e recebe um `device_id` derivado da própria chave pública.
3. a cada ativação o cliente assina um desafio do servidor e recebe uma credencial SOCKS5 nova: usuário e senha de 128 bits vindos de CSPRNG, gerados pelo servidor.
4. a credencial vive no Redis com validade curta, presa ao IP de origem da emissão.

consequências práticas:

- cada instalação tem um segredo diferente, que o operador não escolhe e não conhece;
- credencial capturada expira em minutos e não funciona de outra máquina;
- rotacionar ou trocar de servidor não exige novo build;
- revogar um dispositivo é uma linha no Redis.

### validade é do servidor, não do cliente

o `enabled_until` no registro do Windows é conveniência de interface: um cliente modificado ignora aquele código sem esforço. o que limita a ativação de verdade é o TTL da credencial no servidor, que expira concordando ou não o processo local.

ver [servidor SOCKS5](PROXY-SERVER.md) e [server/README.md](../server/README.md).

### limitações que continuam valendo

- a credencial fica legível em `HKCU\Software\droute` enquanto a sessão dura, para outros processos do mesmo usuário. é aceitável por ser efêmera e presa ao IP, não por ser inacessível.
- o plugin de autenticação do gost só roda no estabelecimento da conexão. um TCP já aberto sobreviveria ao fim do TTL, é o `reaper.sh` que fecha isso, e ele precisa estar ativo.
- proof-of-work encarece o abuso de um atacante único e não resolve abuso distribuído. nada aqui impede alguém de reimplementar o protocolo do cliente, o objetivo é tornar isso caro demais para o que rende.

## superfície de instalação

o instalador grava DLLs na pasta do Discord e usa carregamento local de `version.dll`. esse padrão é legítimo neste projeto, mas também é usado por malware e pode gerar alertas de antivírus. distribua o código-fonte, hashes e instruções reproduzíveis para facilitar auditoria.

arquivos esperados:

- `version.dll` e `droute.dll` na pasta `app-*` atual;
- `Droute.UpdaterHook.dll` e `Update.exe.config` na raiz da variante;
- chave `HKCU\Software\droute\enabled`.
- prazo `HKCU\Software\droute\enabled_until`.
- credencial da sessão em `session_host`, `session_port`, `session_user` e `session_pass`, apagada ao desligar.
- identidade do dispositivo em `%LOCALAPPDATA%\liberar.live\device.key`, protegida por DPAPI.

o comando **remover** apaga esses componentes, desliga o toggle, descarta a identidade do dispositivo e relança o Discord.

## dados e logs

este fork não adiciona telemetria própria. os logs locais podem conter endereços de destino, tempos, códigos de erro e informações operacionais. o logger de configuração registra apenas que autenticação está definida, não a senha em texto claro.

ainda assim, trate `%TEMP%\droute.log` e o `droute.log` da pasta do Discord como dados de diagnóstico e revise-os antes de compartilhar publicamente.

## fail-closed no modo proxy

enquanto o proxy está ligado, conexões IPv6 externas e alguns caminhos UDP diretos são bloqueados para evitar vazamento fora do SOCKS5. loopback e a conexão com o próprio servidor proxy permanecem diretos por necessidade técnica.

ao desligar, o código volta às funções originais do Winsock. ele fecha apenas o controle SOCKS5 das associações UDP, o socket UDP do Discord permanece aberto e passa a usar a rota direta. TCPs abertos pelo proxy recebem `shutdown(SD_BOTH)`, não `closesocket`, e continuam pertencendo ao Discord até o aplicativo encerrá-los.

o registro de TCP usa o mesmo lock de `Mine_closesocket`, impedindo que o Windows libere e reutilize um identificador entre a consulta e o `shutdown`. como WSS é TLS sobre TCP, a DLL não tenta adivinhar quais conexões são WebSockets: todos os TCP que completaram o SOCKS5 `CONNECT` naquela ativação são interrompidos. a reconexão continua sendo comportamento do Discord/Chromium e precisa ser revalidada após mudanças relevantes do cliente.

## limite automático de banda

uma ativação nunca é ilimitada, e o limite é aplicado em duas camadas independentes.

no cliente, `enabled = 1` só vale com `enabled_until` no futuro, com prazo máximo de 5 minutos. fechar o instalador, esquecer o botão ou reiniciar o computador não torna a ativação permanente, prazo ausente ou vencido cai para o modo direto.

no servidor, a credencial daquela ativação tem TTL próprio no Redis. essa é a camada que conta: a primeira é fiscalizada dentro do processo do Discord e pode ser removida por quem recompile o cliente, a segunda não depende de nada que rode na máquina do usuário. a janela local é sempre menor que a do servidor, com margem, para que o usuário nunca perceba o corte no meio.

## assinatura de código e SmartScreen

o executável não é assinado. um binário novo, sem reputação e que grava DLLs dentro de outro programa aciona o SmartScreen do Windows e boa parte dos antivírus. isso é esperado e não indica falha do build.

ao distribuir publicamente:

- publique o SHA-256 de `liberar.live.exe` em um endereço estável junto da versão distribuída;
- mantenha o código-fonte acessível para auditoria;
- documente o aviso do SmartScreen na própria página, em vez de pedir que o usuário desative a proteção;
- avalie um certificado de assinatura de código (custo aproximado de US$ 200/ano) se o volume justificar. um certificado OV leva semanas para acumular reputação, um EV reduz o atrito imediatamente.

nunca instrua o usuário a desligar o antivírus. além de perigoso para ele, é o padrão de comunicação que faz a distribuição inteira ser classificada como maliciosa.

## contexto regulatório (ANPD, agosto de 2026)

em 12/08/2026 a ANPD determinou a suspensão da funcionalidade `Go Live` e de recursos equivalentes de transmissão e compartilhamento de vídeo do Discord para usuários no território brasileiro. o Discord cumpriu em 17/08/2026.

o escopo aplicado pela plataforma é mais amplo do que "lives". segundo a central de ajuda do próprio Discord e a cobertura da imprensa em 17/08, a restrição alcança toda comunicação por vídeo em tempo real: `Go Live`, câmera e compartilhamento de tela em mensagens diretas, mensagens diretas em grupo, canais de voz e canais de palco. texto, servidores e chamadas apenas de áudio continuam funcionando. qualquer texto público deve descrever esse escopo, e não só as lives, porque grande parte dos usuários perdeu a câmera em chamada privada e não sabe que é a mesma medida.

dois pontos do despacho importam diretamente para este projeto:

1. a determinação exige que a plataforma implemente mecanismos tecnicamente eficazes contra burla. ou seja: contornar a restrição é um resultado que a agência declarou querer impedir.
2. em ofício à agência, o próprio Discord afirmou não conseguir conter usuários que utilizem VPN. o uso de VPN por pessoa física não é ilícito no Brasil, e a medida da ANPD é dirigida à plataforma, não ao usuário final.

a distinção que importa para quem distribui este software:

- uso pessoal da ferramenta é roteamento de rede, a mesma categoria de qualquer VPN comercial vendida legalmente no país.
- anunciar publicamente o software como meio de burlar a determinação da ANPD desloca o projeto de "ferramenta de conectividade" para "serviço de contorno de medida regulatória", com o operador identificável como alvo. o caso que originou a medida envolve a morte de uma adolescente, o ambiente de fiscalização e a repercussão pública são severos.

recomendação para qualquer material público (site, README, vídeo, loja):

- descreva a função: roteamento do tráfego do Discord por um servidor SOCKS5;
- não use o enquadramento de "burlar", "driblar a ANPD" ou "derrubar o bloqueio do governo";
- não direcione a comunicação a menores de idade;
- não prometa anonimato, veja [escopo do tráfego](#escopo-do-tráfego).

este documento descreve o funcionamento técnico e o contexto factual, não é aconselhamento jurídico.

## uso responsável

o usuário é responsável por cumprir os termos do Discord, as regras do servidor proxy e a legislação aplicável. a documentação descreve o funcionamento técnico, não garante compatibilidade futura nem ausência de bloqueios por parte de terceiros.

o operador do servidor SOCKS5 responde pelo tráfego que sai do seu IP. mantenha logs mínimos porém suficientes para responder a abuso, bloqueie destinos privados e internos (ver [servidor SOCKS5](PROXY-SERVER.md)) e tenha um canal de contato para denúncias antes de abrir a distribuição ao público.
