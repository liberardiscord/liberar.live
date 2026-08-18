# compilação

## requisitos

- Windows x64;
- Visual Studio ou Build Tools com MSVC x64 e Windows 10/11 SDK;
- .NET Framework 4.8 Developer Pack;
- MSBuild e acesso ao NuGet;
- Go para compilar ou testar o broker.

o projeto nativo usa `v143`, um toolset mais novo pode ser escolhido por
`/p:PlatformToolset=...`. o MinHook necessário está vendorizado em
`external/minhook`, não há submódulo para inicializar.

## configuração pública

o cliente público é deliberadamente não operacional fora do ambiente local:

- `BrokerConfig.cs` usa `http://127.0.0.1:8080`;
- `proxy_config.example.hpp` usa `127.0.0.1:1080` como endereço de reserva;
- `PainelConfig` não possui URL padrão.

para testar outro broker sem alterar o fonte, defina `LIBERAR_BROKER_URL` ou
grave somente a URL em `broker.url` ao lado do executável. para o painel
opcional, use `LIBERAR_PAINEL_URL` ou `painel.url`.

o SOCKS de reserva pode ser alterado numa cópia ignorada pelo Git:

```powershell
Copy-Item droute/src/core/proxy_config.example.hpp droute/src/core/proxy_config.local.hpp
```

nunca versione `broker.url`, `painel.url`, `proxy_config.local.hpp`, `.env`,
chaves ou endereços reais.

## build Release x64

em um Developer PowerShell:

```powershell
msbuild droute.sln /restore /p:Configuration=Release /p:Platform=x64
```

o build executa, nessa ordem:

1. MinHook e payload nativo `droute_64.dll`;
2. cópia do payload para os recursos do updater e instalador;
3. `core` e `updaterHook`;
4. restauração/cópia dos recursos WebView2 a partir do cache NuGet;
5. merge do instalador em `installer/bin/Release/liberar.live.exe`.

as DLLs WebView2 não ficam no código-fonte, o alvo
`StageWebView2PublicResources` copia exatamente a versão declarada no
`PackageReference` antes de o MSBuild gerar os recursos.

## broker Linux

testes:

```powershell
Push-Location server/broker
go test ./...
Pop-Location
```

build reproduzível para a VPS:

```powershell
Push-Location server/broker
$env:CGO_ENABLED = '0'
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
go build -trimpath -ldflags="-s -w" -o ../droute-broker .
Pop-Location
```

`server/droute-broker` é artefato local e está ignorado, envie-o junto de
`reaper.sh` e `install-node.sh`, seguindo `server/README.md`.

## validação antes de publicar

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-public.ps1
msbuild droute.sln /restore /p:Configuration=Release /p:Platform=x64
```

depois do build, rode a verificação de novo com `-AllowBuildArtifacts` para que
ela também leia os binários gerados. se você tem endereços próprios que não
podem aparecer em lugar nenhum, liste-os em `LIBERAR_VERIFY_FORBIDDEN`,
separados por vírgula, ou passe `-ForbiddenStrings`. os valores ficam só no seu
ambiente e nunca são escritos no repositório.

confirme que:

- as três cópias de `droute_64.dll` possuem o mesmo SHA-256;
- `liberar.live.exe` foi gerado sem DLL externa ao lado;
- nenhum endpoint, IP público, caminho local ou material de chave aparece na
  árvore ou nos binários gerados;
- o pacote binário acompanha GPLv3 e os avisos de terceiros.
