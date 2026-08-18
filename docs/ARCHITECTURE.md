# arquitetura

> a restrição que motiva o uso do projeto é aplicada pelo próprio cliente Discord, por geolocalização de rede, e não por bloqueio de operadora ou de DNS, por isso a solução é roteamento de saída, e não desvio de resolução de nomes. contexto completo em [segurança](SECURITY.md#contexto-regulatório-anpd-agosto-de-2026).

## visão geral

```text
liberar.live.exe
  ├─ instala version.dll + droute.dll na pasta app-* do Discord
  ├─ instala Droute.UpdaterHook.dll + Update.exe.config
  ├─ pede credencial ao broker (HTTPS) e grava a sessão em HKCU
  └─ controla HKCU\Software\droute\enabled + enabled_until

Discord.exe
  └─ version.dll local
      └─ droute.dll
          ├─ TCP → SOCKS5 CONNECT → servidor
          └─ UDP → SOCKS5 UDP ASSOCIATE → servidor

VPS
  ├─ gost (SOCKS5, :1080) ── auther plugin ──▸ broker
  ├─ broker (HTTPS público + loopback para o gost)
  └─ Redis (credencial com TTL real)
```

como os hooks vivem dentro dos processos do Discord, o roteamento não alcança outros programas do computador.

esse escopo por processo é a primeira barreira de isolamento da publicidade, a
segunda é explícita no aplicativo: o painel só é iniciado em conexão direta e é
interrompido, descarregado para `about:blank` e mantido desligado antes de a
rota dos Estados Unidos ser ativada. qualquer mudança que amplie o roteamento
(WFP, TUN ou proxy de sistema) também deve excluir `liberar.live.exe` e
`msedgewebview2.exe` como defesa adicional. o operador precisa preservar esse
isolamento ao configurar uma rede externa.

## componentes

| projeto | responsabilidade |
|---|---|
| `installer` | interface WinForms, detecção de estado, instalação, remoção e reinício controlado |
| `core` | localização das variantes do Discord e operações de patch |
| `droute` | DLL nativo x64, hooks Winsock, cliente SOCKS5 e toggle em runtime |
| `updaterHook` | reaplica o payload quando o Squirrel cria uma nova pasta `app-*` |
| `external/minhook` | biblioteca usada para instalar os hooks nativos |
| `server/broker` | serviço Go que registra dispositivos, emite credenciais efêmeras e responde ao auther do gost |

## instalação do patch

para cada variante instalada, o instalador encontra a pasta de maior versão e grava:

- `version.dll`: proxy DLL que encaminha exports da biblioteca real e carrega o payload;
- `droute.dll`: payload nativo com hooks TCP/UDP;
- `Droute.UpdaterHook.dll`: hook usado durante atualizações;
- `Update.exe.config`: configuração que carrega o hook no atualizador.

a detecção **atualização necessária** não olha apenas a existência dos arquivos, ela compara os bytes do payload e do updater hook, além do conteúdo da configuração, com os recursos do executável atual.

## hooks de rede

o payload usa MinHook para interceptar funções de `ws2_32.dll`, incluindo `connect`, `bind`, `sendto`, `recvfrom`, variantes `WSA*` e operações overlapped. também intercepta `CreateProcessW` para manter o patch durante o fluxo de atualização.

### TCP

quando o toggle está ligado, conexões IPv4 externas são substituídas por uma conexão ao servidor SOCKS5, autenticação e comando `CONNECT` para o destino original. loopback e a própria extremidade do proxy não são reencaminhados.

IPv6 externo é bloqueado no modo proxy para impedir vazamento direto. com o toggle desligado, `connect` chama a implementação original do Winsock.

cada TCP que conclui o `CONNECT` pelo proxy entra em um registro próprio, separado do estado de sockets não bloqueantes e do mapa UDP. quando a rota é desligada, a DLL publica primeiro o modo direto e executa `shutdown(SD_BOTH)` nesses sockets sob o mesmo lock usado por `Mine_closesocket`. ela não chama `closesocket`, o handle continua pertencendo ao Discord, que recebe a falha de I/O, fecha a conexão antiga e pode criar outra diretamente. se um handshake termina durante a desativação, o próprio caminho de `connect` detecta a corrida e interrompe aquele socket imediatamente.

### UDP

sockets UDP rastreados recebem uma associação SOCKS5 `UDP ASSOCIATE`. os datagramas de saída são encapsulados no formato SOCKS5 e enviados ao relay, os recebidos são desembrulhados antes de chegar ao Discord.

ao desligar o toggle, `TearDownUdpAssociations()` fecha os canais TCP de controle SOCKS5 e limpa o estado de encapsulamento, impedindo que frames do relay sejam entregues pelo caminho direto. ela não fecha o socket UDP criado pelo Discord, os próximos datagramas usam o mesmo socket e o destino original pela conexão normal do computador.

## emissão de credencial

o SOCKS5 exige usuário e senha, mas nenhum dos dois existe no binário. cada
ativação busca uma credencial nova no broker. o broker escolhe um membro de
`BROKER_PROXY_NODES`, vincula a credencial ao nome desse nó e devolve host e
porta, por isso o `droute.dll` não carrega IP de VPS e o pool pode mudar sem
novo executável.

```text
1. primeira execução   cliente gera par ECDSA P-256, chave privada selada com DPAPI
                       em %LOCALAPPDATA%\liberar.live\device.key
2. registro            POST /v1/register/challenge  → {nonce, difficulty}
                       POST /v1/register            → {device_id}   (proof-of-work)
3. ativação            POST /v1/challenge           → {nonce}
                       POST /v1/session             → {host, port, username, password, expires_in}
4. conexão             gost → POST /socks-auth      → {ok}
```

o `device_id` é derivado da própria chave pública, então o cliente não escolhe sua identidade. a assinatura do passo 3 cobre `"droute-session-v1\0" || device_id || nonce`, o que impede que um desafio de um dispositivo seja reaproveitado por outro.

usuário e senha são 128 bits de CSPRNG gerados pelo servidor, gravados no Redis com `SETEX` e presos ao IP que pediu a emissão. o cliente apenas recebe e repassa. detalhes e limites em [segurança](SECURITY.md#credenciais) e [`server/README.md`](../server/README.md).

## toggle em runtime

o estado usa:

```text
HKCU\Software\droute\enabled       (DWORD)
HKCU\Software\droute\enabled_until (QWORD, Unix time UTC)
HKCU\Software\droute\session_host  (REG_SZ)
HKCU\Software\droute\session_port  (DWORD)
HKCU\Software\droute\session_user  (REG_SZ)
HKCU\Software\droute\session_pass  (REG_SZ)
```

- ausente ou `0`: conexão direta;
- `1` com prazo futuro e credencial presente: proxy ativo;
- `1` sem prazo válido, com prazo expirado ou sem credencial: convertido imediatamente para conexão direta.

a interface grava a credencial, depois o prazo e só então liga o toggle. como o DLL lê o toggle por último, ele nunca observa uma ativação que não consegue completar. o DLL inicia desligado por padrão, consulta os valores a cada 300 ms, carrega a credencial na transição para ligado e a apaga da memória ao desligar. sem credencial legível, a transição é recusada e o `connect` falha com `WSAEACCES` em vez de sair direto.

o prazo local é conveniência de interface, não controle de segurança: um cliente modificado ignora esse código. o que limita a ativação de verdade é o TTL da credencial no servidor. a janela local é sempre menor que a do servidor, com margem, para que o corte nunca aconteça no meio de uma transmissão.

desligar não reinicia o Discord. o socket UDP do aplicativo permanece aberto e volta a enviar diretamente depois que seu canal de controle SOCKS é encerrado. os TCP estabelecidos pelo proxy recebem `shutdown(SD_BOTH)` para provocar a reconexão pelo Winsock original, TCPs novos já nascem diretos.

## reinício para ativação

o fluxo antigo por `Ctrl+R` foi removido porque o recarregamento do Electron não oferece um limite confiável e podia produzir falso positivo.

o fluxo atual:

1. pede a credencial ao broker, grava a sessão, o prazo UTC e o toggle ligado;
2. registra os PIDs atuais de cada variante em execução;
3. encerra todos os processos antigos e confirma a saída;
4. inicia novamente os executáveis das variantes selecionadas;
5. procura uma janela responsiva pertencente a um PID que não existia antes;
6. exige estabilidade contínua por um segundo;
7. libera o estado **liberação ativa**.

durante a liberação, a interface exibe a contagem regressiva. o retorno manual ou automático usa exatamente o mesmo caminho: remove o encapsulamento SOCKS do UDP sem fechar o socket do aplicativo, interrompe os TCP antigos do proxy e volta as novas operações ao Winsock original, sem recarregar o Discord.

o timeout de inicialização é 45 segundos. essa detecção confirma o ciclo de processos e a responsividade da janela, não interpreta elementos internos da interface do Discord.

## atualizações do Discord

o `Update.exe.config` faz o Squirrel carregar `Droute.UpdaterHook.dll`. o hook acompanha a criação do novo processo/pasta e copia `version.dll` e `droute.dll` para a versão nova antes que o Discord seja iniciado.

## logs e diagnóstico

- `%TEMP%\droute.log`: handshake, conexões, associações UDP, toggles e falhas do payload;
- `<raiz do Discord>\droute.log`: eventos do updater hook;
- **opções → detalhes**: log da sessão do instalador.
