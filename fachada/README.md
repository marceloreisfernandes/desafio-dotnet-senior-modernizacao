# Fachada SOAP — Strangler Fig controlado em runtime

Aplicação isolada em .NET 8, preservando o WSDL integral do legado e os bytes SOAP.
Somente `ConsultarNfse` pode usar `LEGACY`, `SHADOW` ou `NEW`. Cancelamento,
recepção/emissão/substituição de RPS e todas as outras ações ficam exclusivamente
no legado, com uma tentativa por requisição. Decisão: [ADR 0001](../docs/adr/0001-fachada-soap-legado.md).

## Configuração e atualização ao vivo

O Compose monta **o diretório** `fachada/config` como `/app/routing:ro`; o arquivo
versionado começa com kill switch ligado e modo `LEGACY`. O processo lê no máximo
16 KiB do arquivo a cada decisão, sem cache permissivo, watcher ou API administrativa.
Ausência, falta de acesso, JSON inválido, propriedades duplicadas/desconhecidas,
operação não suportada, modo inválido, percentual fora de 0..100 ou allowlist
inválida resultam em `LEGACY`. Os campos abaixo são obrigatórios; somente a chave
`ConsultarNfse` é configurável. `Operations: {}` também resulta em legado.

```json
{
  "KillSwitch": false,
  "Operations": {
    "ConsultarNfse": {
      "Mode": "SHADOW",
      "Percentage": 100,
      "Providers": ["10000001"]
    }
  }
}
```

`Providers: []` permite todos os prestadores; a lista preenchida restringe a
seleção à IM exata, após Trim, sem converter zeros. A IM é metadado declarado pelo
cliente, **não autorização**. Não substitui autenticação nem controle de acesso.

Fluxo: kill switch → método/action/estrutura elegíveis → IM na allowlist (ou lista
vazia) → coorte percentual → modo. A coorte é `uint32 big-endian dos primeiros
4 bytes SHA256(UTF8("ConsultarNfse:" + IM)) % 100`. Seleciona quando menor que
`Percentage`: 0 nunca, 100 sempre, 50 divide os buckets estáveis; não promete
metade exata do tráfego nem dos prestadores reais. A mesma IM permanece na coorte
entre requisições, processos e reinícios. Não se armazena nem registra o hash.

Para atualizar, salve o JSON completo em arquivo temporário no mesmo diretório e
substitua atomicamente, com permissões que permitam a leitura pelo usuário do
container (e acesso de escrita apenas ao operador autorizado):

```bash
# Prepare/valide routing.next.json com o formato acima; ajuste Mode/Percentage/Providers.
python3 -m json.tool fachada/config/routing.next.json > /dev/null
mv fachada/config/routing.next.json fachada/config/routing.json
```

Não altere parcialmente o arquivo ativo. Uma leitura durante escrita incompleta
vai para legado, mas a substituição atômica fornece snapshots completos. Não
montar apenas o arquivo: um bind mount de inode pode ocultar a troca por rename.
Não remover/recriar containers para alternar flags. Nenhum deploy é necessário
após a instalação inicial desta versão/mount.

**Kill switch:** substitua o JSON por `{"KillSwitch":true,"Operations":{}}`.
A próxima decisão após o rename vê legado; não há intervalo de polling. A leitura
local costuma levar milissegundos; validar o filesystem/mount no ambiente alvo
para o objetivo operacional de poucos segundos. Não há garantia de tempo real
se o host/volume estiver travado. Uma segunda leitura depois de receber/inspecionar
o corpo evita usar a flag antiga durante uploads. Requisições já despachadas e
sombras já admitidas terminam com seus limites; não são repetidas ou revertidas.

Somente um POST SOAP 1.1 com SOAPAction canônica `http://notacarioca.rio.gov.br/ConsultarNfse`,
wrapper `ConsultarNfseRequest/inputXML`, IM inequívoca, sem query/compressão e
Content-Length conhecido até 1 MiB pode sair do legado. XML/UTF-8 inválido,
DTD, profundidade >64, wrapper/action divergentes, parâmetros ambíguos,
corpos grandes/chunked e ações desconhecidas seguem opacos ao legado.
Isso restringe a migração; não rejeita nem reserializa essas chamadas.

## Sombra, falhas e segurança

`SHADOW` entrega status, headers e bytes do legado. Captura em memória até 2 MiB
de resposta e só enfileira a consulta nova após a conclusão da cópia legada.
Há duas vagas/consumidores por padrão; a reserva inclui o tempo no legado.
Sem vaga, a sombra é descartada imediatamente. A fila também é limitada pelo
mesmo número, sem tarefas por requisição, retries ou persistência em disco.
Apenas o corpo da consulta, Content-Type e a SOAPAction fixa são enviados na
sombra; cookies, credenciais e demais headers do cliente não são copiados.

Prazo padrão da sombra: 2000 ms, incluindo resposta. Resposta moderna >2 MiB ou
comprimida encerra a observação. Resposta legada incompleta, comprimida ou maior
que 2 MiB não gera sombra. Timeout, erro e saturação não mudam a resposta pública.
O cancelamento HTTP limita o trabalho da fachada, mas não garante interrupção
imediata do SELECT remoto/ODP.NET; limites de pool e comando do serviço continuam
necessários. Leituras duplicadas podem aumentar a carga Oracle.

A comparação usa status, Content-Type e bytes, sem normalizar/remover campos.
Logs têm somente categorias fixas: `parity`, `unexpected-divergence`,
`expected-strict-xml`, `capacity-skipped`, `timeout-or-stop`, `failure`, `response-limit`.
A exceção esperada é estreita: referência a controle XML inválido documentada na
ADR 0002, legado 200 com notas e novo 200 com o E001 exato. Outros resultados são
inesperados, inclusive mudanças de valores/ordem e mensagens internas genéricas.
DTD/profundidade não são expandidos para obter metadados e ficam no legado.
Não há payloads, IM/CPF/CNPJ, hashes de identificadores ou exceções nos logs próprios;
dados transitam em memória somente enquanto a requisição/observação existe.
Não habilitar logging de corpos/headers ou dumps de processo com dados fiscais.
O logging preexistente do legado permanece uma limitação externa.

`NEW` encaminha uma vez ao serviço moderno e entrega sua resposta SOAP. Falha de
transporte = 502; timeout = 504; resposta parcial aborta a conexão. Não existe
fallback para legado após resultado ambíguo. Acione o kill switch para as chamadas
seguintes. Health/readiness continuam verificando legado, não certificam o moderno.
O WSDL público continua vindo do legado e só seu endereço é reescrito.

## Instalação e verificação

Configuração de transporte/capacidade é de startup, separada das flags ao vivo:

| Variável | Padrão | Finalidade |
| --- | --- | --- |
| `Routing__File` | `routing.json` no content root | Arquivo local de decisões; Compose usa `/app/routing/routing.json` |
| `Legacy__BaseUrl` | `http://webservices:8080` | Origem legada fixa |
| `Legacy__PublicServiceUrl` | `http://localhost:8080/NfseWS.asmx` | Endereço WSDL público |
| `Legacy__TimeoutSeconds` | `660` | Limite total legado, 1..660 s; Compose aceita `LEGACY_TIMEOUT_SECONDS` |
| `Modern__BaseUrl` | `http://consulta:8080` | Origem moderna fixa |
| `Modern__TimeoutSeconds` | `30` | Limite total NEW, 1..300 s |
| `Modern__ShadowTimeoutMilliseconds` | `2000` | Prazo sombra, 50..10000 ms |
| `Modern__ShadowConcurrency` | `2` | Vagas/consumidores por processo, 1..8 |

Com os serviços existentes já saudáveis, instalar somente a nova fachada:

```bash
dotnet build fachada/src/NotaCarioca.Fachada -c Release
dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --filter 'TestCategory!=Oracle'
dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --filter 'TestCategory!=Oracle'
dotnet test legado/tests/NotaCarioca.Testes -c Release -f net8.0
docker compose config --quiet
docker compose build fachada
docker compose up -d --no-deps fachada
```

A instalação inicial recria somente a fachada e pode interromper conexões nela;
Oracle, legado, consulta e volumes não precisam ser recriados. Para demonstrar sem
alterar o endpoint/container existente, o teste abaixo inicia uma fachada efêmera
em loopback e troca um JSON temporário, com os upstreams reais já ativos:

```bash
# Obtenha as origens atuais por docker inspect; não use IPs históricos.
NFSE_LEGACY_BASE_URL=http://IP_LEGADO:8080 \
NFSE_MODERN_BASE_URL=http://IP_CONSULTA:8080 \
dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --filter 'TestCategory=Oracle' --logger 'console;verbosity=normal'
```

Esse teste usa o exemplo SOAP `04`, o caso documentado de caractere inválido,
paridade LEGACY/NEW/SHADOW e kill switch, sem escritas fiscais. Sem variáveis ele
fica explicitamente ignorado. Os outros testes usam upstreams falsos e demonstram
allowlist, 0/50/100, alterações ao vivo, JSON inválido, escritas no legado, sombra
lenta/falha/saturada e ausência de fallback em NEW. A suíte anterior continua
verificando headers, WSDL integral, bytes opacos, health e falhas de streaming.
Não executar o runner geral `tools/exemplos/executar.sh`, que inclui escritas.

Execução anterior, com fachada efêmera, em 19/10/2026, SDK 8.0.131: fachada **47/47**, incluindo integração
SOAP/Oracle e WSDL integral com NEW; consulta existente **86/86** locais e **7/7**
Oracle (incluindo 40 casos de referência); legado net8.0 **16 aprovados, 1 skip**
preexistente (`Parse_MesPorExtenso`). No upstream controlado, troca do kill switch
até resposta legada em **2 ms** e 12 respostas legadas em **44 ms** sob sombra
lenta com deadline de 1000 ms/pico de 1 execução. São medições locais, não SLA.
A integração real confirmou `parity` e `expected-strict-xml`, preservando a resposta
legada. Usou fachada efêmera; os containers existentes não foram recriados nem
atualizados. Nenhuma chamada fiscal de escrita foi executada.

## Validação da imagem Compose em 19/10/2026

Após ajuste do timeout, `docker compose build fachada` e atualização operacional
aprovada com `docker compose up -d --no-deps --no-build fachada`, o container
`b4e8436093da` executou a imagem
`sha256:54b0ff8f15083f744d143a63bc3cda9b169b3ef1ee5c266f3a37a650264c31a2`,
com mount somente leitura de `/app/routing`. Oracle, legado e consulta mantiveram
IDs, imagens, horários de início e mounts. Somente a fachada foi recriada.

No endpoint Compose real, consultas confirmaram LEGACY padrão, NEW e SHADOW,
trocas atômicas ao vivo, kill switch (9,57 ms até a resposta legada subsequente),
e fallback seguro para configuração inválida/ausente. A fixture 04 preservou
status, Content-Type e os 4361 bytes legados. A consulta com referência XML de
controle já documentada distinguiu o despacho: NEW retornou os 584 bytes com E001
do moderno; SHADOW retornou os mesmos 4361 bytes legados. Logs confirmaram
`parity` e `expected-strict-xml`. WSDL integral comparado estruturalmente ao legado,
alterando somente `soap:address`, inclusive em NEW. Nenhuma reinicialização entre
as trocas: mesmo ID, imagem e horário de início. Estado final restaurado:
`KillSwitch=true`, `Mode=LEGACY`, `Percentage=0`, `Providers=[]`.
`/health/live`, `/health` e `/health/ready` responderam 200/OK. O Compose não define
HEALTHCHECK para a fachada; essa evidência vem de sondas HTTP reais.

A mesma imagem foi executada em container de teste efêmero, sem volumes de dados,
com dois upstreams HTTP falsos em loopback. Em NEW e SHADOW, cinco ações de escrita
sintéticas por modo (`CancelarNfse`, `RecepcionarLoteRps`, `GerarNfse`,
`SubstituirNfse`, `EnviarLoteRps`) tiveram uma chamada legada cada e zero modernas.
Com override legado de 1 s, timeout antes de headers retornou 504 em 1,012 s;
stream contínuo foi abortado em 1,007 s, sem renovar o prazo total; cancelamento
POST desconectou o upstream em 2 ms; transporte rompido retornou 502. Uma tentativa
em cada caso, sem retry. Container de teste removido após execução. Essas escritas
sintéticas nunca foram enviadas ao Compose fiscal real. Não houve emissão,
cancelamento ou submissão real de RPS. O orçamento de 660 s foi conferido na
configuração; não foi feita uma espera de 600/660 s nem uma escrita fiscal lenta.

Regressões atuais, Release, SDK 8.0.131, sem restore: fachada **47/47** sem Oracle
(incluindo cancelamento GET/POST), consulta **86/86** sem Oracle, legado net8.0
**16 aprovados e 1 skip** preexistente; verificador Python **2/2**.
As consultas reais acima complementam os testes locais; não representam execução
da suíte Oracle completa nesta rodada. Os scripts e resultados detalhados desta
rodada ficaram em `/tmp/nfse-compose-validation*` e `/tmp/nfse-image-controlled*`.

## Reversibilidade e limites de produção

Ative o kill switch antes de parar o moderno. Confirme health, WSDL e consulta
legada. Não há migração de dados nem rollback fiscal. Para retirar toda a fachada,
a devolução da porta ao legado exige uma interrupção operacional separada.
Nunca executar down/down -v/prune/just down nem remover volumes para reverter.

Arquivo local e vagas são **por réplica**. Múltiplas réplicas não recebem updates
atômicos em conjunto; cada uma precisa de distribuição/controlador operacional
confiável. Não há auditoria central de flags, métricas persistentes, SLA de volume
remoto, certificação de carga, TLS ou autenticação nova. O serviço moderno deve
continuar interno; sua credencial Oracle de desenvolvimento ainda tem permissões
de proprietário, e SQL somente SELECT não substitui credencial restrita.

O timeout legado padrão é **660 segundos**, configurável entre 1 e 660 s. Substitui
o padrão de 120 s e o teto de 300 s, que encurtavam o orçamento de 600 s do cliente
`tools/enviar-lote.sh`. A margem de 60 s evita que a fachada seja o primeiro limite
para esse cliente; não amplia o prazo do próprio cliente. No Compose, configure
`LEGACY_TIMEOUT_SECONDS` antes de recriar apenas a fachada. Esse ajuste é de startup.
O prazo total inclui upload e download, mesmo com streaming ativo; não se renova
a cada chunk. Conexão continua limitada a 5 s e desconexão do cliente cancela o
transporte. O host original não tinha limite total equivalente: operações longas
podem concluir no legado após 504/desconexão. Cancelar HTTP não garante interromper
Oracle nem desfazer efeitos. O limite finito retém recursos por até 11 minutos;
clientes, gateways e proxies precisam ter seus próprios orçamentos conciliados.
Não foram executadas escritas fiscais lentas nem se garante compatibilidade temporal
irrestrita. Nunca repetir emissão,
cancelamento ou lote automaticamente; reconciliar o resultado por consulta e
procedimento fiscal existente. NEW tem limite próprio de 30 s; diferenças estritas
de XML e limites do serviço moderno permanecem as da ADR 0002. Leituras em momentos
diferentes podem divergir sob alterações concorrentes; a comparação não fornece
snapshot Oracle comum nem prova de paridade para toda a população.
