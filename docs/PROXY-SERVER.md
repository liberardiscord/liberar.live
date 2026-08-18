# servidor SOCKS5

## requisitos

o cliente precisa de um servidor SOCKS5 que ofereça:

- autenticação por usuário e senha;
- comando `CONNECT` para TCP;
- comando `UDP ASSOCIATE` e relay UDP acessível;
- tempo de resposta compatível com voz em tempo real;
- validação da credencial a cada conexão contra uma fonte externa, porque a credencial deste projeto nasce e morre a cada ativação.

um proxy HTTP, inclusive Squid, pode transportar parte do tráfego TCP, mas não substitui o relay UDP exigido por voz/RTC.

## por que não Dante

a configuração de referência deste projeto usava Dante com `socksmethod: username`. esse método resolve a credencial contra usuários do sistema, e o desenho atual emite usuário e senha aleatórios com validade de minutos. não dá para transformar cada ativação numa conta de sistema, nem para revogá-la em tempo hábil.

Dante continua sendo um servidor correto para o caso de conta fixa, ele deixou de servir aqui por causa do modelo de credencial, não por desempenho.

## gost com auther plugin

o arquivo de referência é [`server/gost.yaml.example`](../server/gost.yaml.example). a peça central é o auther em modo plugin:

```yaml
handler:
  type: socks5
  auther: broker
  metadata:
    udp: true            # sem isto não há UDP ASSOCIATE, e voz e vídeo não passam
    udpBufferSize: 4096

authers:
  - name: broker
    plugin:
      type: http
      addr: http://127.0.0.1:8000/socks-auth
      timeout: 3s
```

a cada conexão o gost chama o broker com `{"service","username","password","client":"IP:porta"}` e obedece a resposta `{"ok":bool}`. o broker consulta o Redis, onde a credencial daquela ativação foi gravada com `SETEX`, ou seja, com TTL real, e compara o IP de origem com o IP para o qual a credencial foi emitida.

use o modo plugin, não o data source Redis do próprio gost: aquele data source guarda as credenciais num Hash, que não tem TTL por campo. as credenciais nunca expirariam sozinhas, e o desenho inteiro cairia em silêncio.

o bypass de redes privadas e o `climiter` do exemplo cobrem, respectivamente, uso do nó como porta para a rede interna do provedor e conexões simultâneas por IP de origem. o procedimento completo de instalação, com Redis, broker e unidades systemd, está em [`server/README.md`](../server/README.md).

## implementações alternativas

não presuma que autenticação SOCKS5 no controle TCP também protege automaticamente o relay UDP. o `shadowsocks-go`, por exemplo, implementa usuário/senha e um relay UDP otimizado, mas o próprio tipo `Socks5ServerConfig` alerta que seus listeners UDP SOCKS5 não são protegidos por essa autenticação. expor essa configuração diretamente à Internet pode criar um relay UDP utilizável sem credencial.

por isso, `shadowsocks-go` não é substituto direto do gost para o cliente atual. ele só deve ser considerado depois de uma destas mudanças:

- implementar e auditar no servidor uma associação UDP vinculada à sessão TCP autenticada;
- alterar o cliente para Shadowsocks 2022, cujo UDP é autenticado e criptografado;
- colocar uma camada de acesso equivalente que não deixe o listener UDP público e anônimo.

qualquer substituto precisa, além disso, aceitar autenticação delegada a um serviço externo, sem isso não há como impor validade curta por credencial.

desempenho de loopback não substitui a verificação desse modelo de segurança nem um benchmark representativo no nó escolhido.

## firewall

TCP e UDP precisam chegar à porta do gost. sempre que possível:

- restrinja as origens no firewall;
- mantenha o Redis apenas em loopback e o listener de autenticação do broker em
  loopback ou numa interface privada/WireGuard acessível somente pelos nós. um
  endpoint de validação de credencial exposto à internet é um oráculo de
  adivinhação;
- limite taxa e conexões simultâneas;
- monitore tentativas de autenticação;
- não exponha bancos, SMTP, painéis ou redes privadas pelo proxy.

bloqueio de portas de abuso pertence ao firewall, não ao `gost.yaml`: um erro de digitação no YAML falha aberto, enquanto uma regra de saída no nftables falha fechada.

o proxy pode coexistir com outros serviços da VPS, desde que a configuração e as regras de firewall sejam específicas. reiniciar o gost não deve exigir parar serviços não relacionados.

## validação

antes de distribuir o cliente:

1. rode `go test ./...` em `server/broker`: a suíte cobre TTL, vínculo de IP, uso único de desafio e cotas contra um Redis em memória, e falha antes de você gastar tempo com a VPS;
2. teste uma conexão SOCKS5 autenticada por TCP com uma credencial recém-emitida;
3. confirme `UDP ASSOCIATE` a partir de uma rede externa;
4. repita a mesma credencial de outra máquina: deve falhar por vínculo de IP;
5. espere o TTL vencer e repita da máquina original: deve falhar;
6. confirme que uma segunda ativação recebe credencial diferente da primeira;
7. verifique os logs do gost e do broker durante uma chamada;
8. confirme que destinos privados estão bloqueados;
9. deixe uma conexão TCP aberta até o TTL vencer e confirme que o `reaper.sh` a derruba;
10. valide que nenhum serviço não relacionado foi interrompido.

os itens 4, 5 e 9 são os que provam que o limite deixou de ser client-side. sem eles, o resto do teste passa mesmo com a imposição do servidor desligada.

o listener do gost precisa ser IPv4 explícito (`addr: "0.0.0.0:1080"`), nunca `":1080"`. um listener dual-stack (`[::]`) reporta os pares como IPv4-mapeado (`::ffff:192.0.2.4`), enquanto o broker guarda o IP de origem em forma plana (`192.0.2.4`). o `reaper.sh` compara os dois: sob dual-stack nenhuma conexão viva casaria com a lista de ativos, e ele derrubaria todo usuário legítimo a cada passada, não só as vencidas. o `reaper.sh` ainda normaliza `::ffff:` por segurança, mas a correção primária é o bind. isso foi validado em campo: os cinco caminhos (ativação, TCP, vínculo de IP, `UDP ASSOCIATE` e o corte pelo reaper) passam com o bind IPv4.

## distribuição pública

o binário não carrega mais credencial alguma (ver [segurança](SECURITY.md#credenciais)), então o risco antigo, alguém extrair a conta do executável e publicar um proxy aberto, deixou de existir na forma em que existia. o risco que sobra é outro: um cliente reimplementado pedindo credenciais legítimas em laço.

antes de abrir a distribuição:

- limite por dispositivo e por IP de origem: teto diário por dispositivo e conexões simultâneas. o intervalo mínimo entre emissões vem desligado por padrão (`BROKER_SESSION_MIN_INTERVAL=0`) de propósito: clicar "liberar" nunca pode ser recusado, e como cada credencial já nasce presa ao IP e com TTL curto, reemitir pro mesmo dispositivo não dá capacidade nova. ligue o intervalo só se aparecer abuso, ele é um regulador de vazão, não um controle de segurança. o teto diário é alto (`2000`) apenas como freio contra um cliente em laço, longe do que um humano clicando alcança;
- bloqueie destinos que não sejam do Discord. o caso de uso é conhecido e restrito, permitir saída irrestrita transforma o servidor em proxy aberto e o operador em responsável pelo que passar por ele;
- bloqueie portas de abuso (25/SMTP em especial) antes de qualquer regra de saída;
- monitore a taxa de falhas de autenticação: um pico indica alguém testando credenciais ou sondando o endpoint;
- mantenha o `reaper.sh` ativo. o auther só roda no estabelecimento da conexão, sem o reaper, um TCP já aberto sobrevive ao fim do TTL;
- tenha um endereço de abuso publicado e monitorado antes do primeiro download público.

rotação deixou de exigir novo build. o operador configura o endereço do broker,
como `https://api.example.com`, host, porta, usuário e senha do proxy chegam na
resposta de `/v1/session`. o host pode ser IPv4 ou DNS. trocar ou adicionar um
nó é uma mudança em `BROKER_PROXY_NODES` no servidor, não no cliente.

dados de localização, aliases SSH, endereços reais e credenciais pertencem
somente ao inventário privado do operador e nunca ao repositório público.

## expor a API do broker ao cliente

o broker tem dois listeners. o de autenticação (`/socks-auth`, `/active-ips`)
fica em loopback numa instalação de um nó. num pool, ele pode escutar apenas no
IP privado/WireGuard usado pelos nós, nunca na interface pública. o outro, a API
do cliente (`/v1/register`, `/v1/challenge`, `/v1/session`, `/healthz`), nasce em
loopback (`127.0.0.1:8080`) atrás do terminador TLS e é o único que o cliente
Windows alcança pela Internet.

para publicá-lo, ponha um terminador de TLS em `api.example.com` encaminhando
`443 → 127.0.0.1:8080`: um `server` block em nginx, ou um Caddy que provisione o
certificado sozinho. o cliente já usa esse nome estável, mudar o IP do broker é
alterar DNS, não recompilar. o certificado pode ser fixado em
`PinnedRootThumbprints` pela raiz emissora.

só a API do cliente vai a público. o endpoint de autenticação continua privado: expô-lo transforma a validação de credencial em oráculo de adivinhação. o registro exposto é defendido pelo proof-of-work, que escala por /24, e pelo teto de falhas por IP, não é um oráculo, mas monitore mesmo assim a taxa de erro nele.

## vários nós e aposentadoria do Dante

um nó dedicado só a proxy, sem e-mail, banco ou qualquer coisa competindo por CPU, RAM e banda, rende muito mais que um box compartilhado. o caminho de escala é horizontal: um broker e um Redis centrais, e N nós rodando apenas gost e reaper.

o pool já é configurável por ambiente:

```dotenv
BROKER_PROXY_NODES=socks5=proxy-us-1.example.com:1080,socks5-us-2=proxy-us-2.example.com:1080
```

o broker seleciona em round-robin e vincula cada credencial ao nome do nó. o
gost envia seu `services[].name` ao auther, que recusa uma credencial destinada
a outro nó. duas restrições operacionais permanecem:

- cada nó remoto precisa alcançar o `/socks-auth` do broker por uma rede privada (WireGuard, VPN do provedor), jamais pela internet;
- cada reaper consulta `/active-ips?node=SEU_NOME`. sem esse filtro, uma sessão
  ativa em outro nó poderia manter indevidamente um socket vencido.

a ordem do cutover importa, para não derrubar quem já usa. o gost substitui o Dante pelo modelo de credencial, não por velocidade: os dois têm desempenho parecido, e num proxy de vídeo o gargalo é banda muito antes do servidor SOCKS. por isso o Dante só deve ser aposentado nesta ordem: (1) validar o gost de ponta a ponta em porta paralela; (2) distribuir o cliente novo, que fala com o broker; e só então (3) desligar o Dante e mover o gost para a porta padrão. os clientes antigos carregavam a credencial fixa e apontam para o Dante, desligá-lo antes do passo 2 deixa todo mundo sem proxy funcional, porque o gost recusa a credencial antiga.

## segredos

não salve host, credencial nem chave real neste documento ou em qualquer arquivo versionado. os exemplos em `server/` são deliberadamente vazios de valor operacional.

o cliente não contém segredo a rotacionar. o que precisa de cuidado agora é o material do servidor: certificado do broker, senha do Redis se houver, e as chaves públicas registradas dos dispositivos.
