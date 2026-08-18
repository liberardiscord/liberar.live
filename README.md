# liberar.live

o liberar.live faz só o Discord sair por um servidor nos Estados Unidos, o resto
do computador continua como estava, sem VPN ligada no sistema e sem proxy
global. por baixo são duas coisas simples: `SOCKS5 CONNECT` para o tráfego
normal e `SOCKS5 UDP ASSOCIATE` para voz e vídeo.

aqui está o programa do Windows e o servidor que entrega as credenciais
temporárias, não está aqui o site, a infraestrutura de produção, segredo nenhum,
nem executável pronto para baixar.

## já vem seguro

se você compilar isto agora e rodar, ele não fala com servidor nenhum além do
seu próprio computador:

| o quê | padrão |
|---|---|
| broker | `http://127.0.0.1:8080` |
| proxy de reserva | `127.0.0.1:1080` |
| painel de anúncio | desligado |
| credenciais | pedidas ao broker na hora, nunca gravadas dentro do executável |

quando quiser apontar para o seu servidor, tem dois caminhos: a variável
`LIBERAR_BROKER_URL` ou um arquivo `broker.url` ao lado do executável. o painel
opcional funciona igual, com `LIBERAR_PAINEL_URL` ou `painel.url`. esses
arquivos são seus e já estão no `.gitignore`, então não vão parar em commit sem
querer.

## o que tem em cada pasta

| pasta | o que é |
|---|---|
| `core`, `droute`, `installer`, `updaterHook` | o programa do Windows |
| `external/minhook` | o pedaço do MinHook que a compilação precisa |
| `server` | broker em Go, gost, reaper e o instalador do nó |
| `docs` | arquitetura, compilação, servidor e segurança |
| `scripts` | a verificação contra dado privado e arquivo de build |

## compilar o programa

você precisa de Windows x64, Visual Studio ou Build Tools com MSVC x64 e Windows
SDK, o Developer Pack do .NET Framework 4.8 e o MSBuild.

```powershell
msbuild droute.sln /restore /p:Configuration=Release /p:Platform=x64
```

o resultado sai em `installer/bin/Release/liberar.live.exe`. o WebView2 vem pelo
NuGet na hora, e todas as DLLs intermediárias nascem na sua máquina, nenhuma
delas está guardada aqui. requisitos e detalhes em
[compilação](docs/BUILDING.md).

## compilar o servidor

```powershell
Push-Location server/broker
$env:CGO_ENABLED = '0'
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
go build -trimpath -ldflags="-s -w" -o ../droute-broker .
Pop-Location
```

depois é mandar `server/droute-broker`, `server/reaper.sh` e
`server/install-node.sh` para uma máquina Debian ou Ubuntu e rodar o instalador
com os seus próprios `API_DOMAIN` e `PROXY_DOMAIN`. o segredo do Redis nasce lá
dentro, na própria máquina, e nunca passa por aqui. o passo a passo está em
[servidor](server/README.md).

## antes de publicar qualquer coisa

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-public.ps1
```

o script recusa arquivo de build, chave, configuração privada, caminho de
usuário, subdomínio operacional e IP público fora das faixas de documentação. se
você tem valores seus que nunca podem aparecer, liste em
`LIBERAR_VERIFY_FORBIDDEN`, separados por vírgula, e ele procura por eles
também. nenhum desses valores fica escrito no repositório.

## documentação

- [arquitetura](docs/ARCHITECTURE.md), como o desvio do Discord funciona
- [compilação](docs/BUILDING.md), requisitos e toolsets
- [servidor e protocolo](docs/PROXY-SERVER.md), gost, broker e o que trafega
- [segurança](docs/SECURITY.md), por que não existe segredo dentro do executável

## contribuindo

pull request é bem-vindo. antes de abrir, dá uma olhada em
[CONTRIBUTING.md](CONTRIBUTING.md), a lista é curta, e o principal é nunca
mandar host, IP, usuário, senha ou `.env`.

## origem e licença

a base veio do [Droute](https://codeberg.org/snowluwu/droute), a ideia do
[force-proxy](https://github.com/runetfreedom/force-proxy) e os hooks do
[MinHook](https://github.com/TsudaKageyu/minhook), nenhum deles tem vínculo com
este projeto nem o endossa.

distribuído sob a GNU General Public License v3. veja [LICENSE.txt](LICENSE.txt)
e [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
