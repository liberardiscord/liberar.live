# contribuindo

contribuições são bem-vindas por pull request. mantenha as alterações focadas,
explique o comportamento observado e inclua os testes executados.

## preparação

```powershell
git clone <URL-DO-REPOSITORIO>
cd liberar.live
msbuild droute.sln /restore /p:Configuration=Release /p:Platform=x64
```

consulte [compilação](docs/BUILDING.md) para requisitos e toolsets.

## configuração privada

nunca envie host, endereço IP, usuário, senha, chave SSH ou arquivo `.env`. para
testes próprios, copie `droute/src/core/proxy_config.example.hpp` para
`proxy_config.local.hpp`, o destino é ignorado pelo Git.

se você opera um servidor e quer garantir que os seus endereços nunca escapem no
diff, liste-os em `LIBERAR_VERIFY_FORBIDDEN`, separados por vírgula, antes de
rodar a verificação do passo 4. eles ficam só no seu ambiente e o script procura
por eles na árvore inteira.

antes de abrir um pull request:

1. execute `git diff --check`;
2. confira `git status --short`;
3. procure segredos e dados pessoais no diff;
4. execute `powershell -ExecutionPolicy Bypass -File scripts/verify-public.ps1`;
5. compile em Release x64;
6. rode `go test ./...` em `server/broker` se tocou no broker;
7. descreva qualquer interação manual realizada com o Discord.
