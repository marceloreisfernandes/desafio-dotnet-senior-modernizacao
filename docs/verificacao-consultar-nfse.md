# Verificação local — ConsultarNfse independente

Execução em 2026-10-19. Evidência local da réplica; não certifica produção ou
paridade integral. Decisões: [ADR 0002](adr/0002-consultar-nfse-independente.md).
Execução/contratos: [novo/README.md](../novo/README.md).

## Identidade e preservação

Repositório: `desafio-dotnet-senior-modernizacao` (raiz do checkout).
Branch: `feat/fachada-soap-legado`.
HEAD inicial: `229f6278056d250d641d61044bec6ee46ddeea99`, igual ao solicitado;
`git status --short` inicialmente vazio.
Origin: `git@github.com:marceloreisfernandes/desafio-dotnet-senior-modernizacao.git`;
upstream: `https://github.com/prefeitura-rio/desafio-dotnet-senior-modernizacao.git`.
Nenhum fetch, staging, commit, push, PR ou alteração de branch.

`git diff --exit-code -- legado db fachada tools/exemplos Dockerfile` e
`git diff --cached --exit-code` retornaram 0. Código, testes e dados representativos
existentes foram preservados. A alteração do Compose acrescenta apenas o bloco
`consulta`. A fachada continua apontando exclusivamente para `webservices`.

## Caracterização antes da implementação

Investigados host SOAP/WSDL, serviço de consulta, leitor XML, construtor XML,
validador VB.NET, repos de contribuintes/notas, parâmetros ODP.NET, schema,
índices, seeds e relação com packages/triggers. A consulta não chama PL/SQL
de emissão/cancelamento.

Primeiro foram criados corpus/verificador, baseline e 7 testes de caracterização,
executados antes de criar o projeto de produção novo. Os 40 casos do corpus
passaram contra a entrada pública da fachada transparente, conferida depois
contra o host legado direto.

```bash
python3 tools/verificar-consultar-nfse.py --legacy http://127.0.0.1:8080 --report docs/evidencias/consultar-nfse-baseline.json
dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release -p:NuGetAudit=false --logger 'console;verbosity=minimal'
```

Resultados: exit 0; 40/40 casos com expectativas satisfeitas; 7/7 testes iniciais.
O projeto de testes inicialmente referenciava somente o serviço legado. Depois
foi acrescentada a referência ao projeto novo para comparação. O baseline
[consultar-nfse-baseline.json](evidencias/consultar-nfse-baseline.json) contém
somente hashes, status/content type, tamanhos, contagem de notas, códigos de erro
e indicação de próxima página, sem respostas/dados pessoais.

## Regras e contratos

O SOAP preserva: precedência prestador → documento → período; prestador ativo
buscado por IM com Trim e CNPJ ignorado; normalização permissiva do documento;
datas invariáveis truncadas; fim exclusivo; diferença de datas até 31 dias;
filtro adicional de competência; página inválida como 1; aritmética unchecked
Int32; ordenação emissão/ID; 50 + 1; E040/200 para vazio; notas canceladas e
substituídas; formatos/escape/ordem XML, códigos e mensagens; documentos do
tomador sem zeros iniciais e ISS persistido sem recálculo.

REST explicita as diferenças: datas inclusivas de até 31 dias, entrada estrita,
página 1..1000000 com Int64, vazio 200, prestador ausente/inativo 404, documento
como string original, situação textual, ProblemDetails sem detalhes internos.
Endpoints e schemas estão no [OpenAPI](../novo/src/NotaCarioca.Consulta/openapi.json).
SOAP é `/NfseWS.asmx`, apenas ConsultarNfse; seu WSDL é interno e limitado a essa
operação. Health live/ready e `/openapi/v1.json` também foram implementados.

## Testes e builds

SDK local `8.0.131`, runtimes .NET/ASP.NET `8.0.31`, VSTest `17.8.0`.
O build usa warnings como erros nos projetos novos.

Variáveis da integração real nesta execução:

```bash
export NFSE_TEST_CONNECTION_STRING="$NFSE_CONNECTION_STRING"
export NFSE_LEGACY_BASE_URL=http://127.0.0.1:8080
```

NFSE_CONNECTION_STRING deve conter a conexão da réplica de desenvolvimento,
conforme o Compose existente. O valor foi omitido deste relatório.
Não houve introdução/uso de credencial de produção.

| Comando | Evidência |
| --- | --- |
| `dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --no-restore --logger 'trx;LogFileName=consulta-oracle.trx' --logger 'console;verbosity=minimal'` | exit 0; 76 passaram, 0 ignorados/0 falhas; 5 testes Oracle; duração reportada 40 s |
| `dotnet build legado/NotaCarioca.sln -c Release --no-restore` | exit 0; net48 + net8.0; 0 warnings/0 erros |
| `dotnet test legado/tests/NotaCarioca.Testes -c Release -f net8.0 --no-build --logger 'trx;LogFileName=legado.trx' --logger 'console;verbosity=minimal'` | exit 0; 16 passaram, 1 ignorado (`Parse_MesPorExtenso` preexistente), 0 falhas |
| `dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --no-restore --logger 'trx;LogFileName=fachada.trx' --logger 'console;verbosity=minimal'` | exit 0; 24 passaram, 0 ignorados/0 falhas |
| `python3 -m unittest discover -s tools/tests -v` | exit 0; 2 passaram; verificador preexistente |
| `docker compose config --quiet` | exit 0 |
| `docker compose build consulta` | exit 0; build/publish da imagem final |
| `docker compose up -d --no-deps consulta` | exit 0; somente consulta criado e depois atualizado; sem recriação das dependências |
| `python3 tools/verificar-fachada.py --legacy http://ORIGEM_LEGADO:8080` | exit 0; ConsultarNfse e ConsultarNfseServicoTomado idênticos ao legado; health e WSDL aprovados |
| `git diff --check` | exit 0 |

Os 5 testes de categoria Oracle não foram ignorados na execução real. Cobrem
40 requisições golden contra o host original e o novo Kestrel, comparação com
baseline, valores persistidos, inclusão de um único dia REST/zeros preservados,
vinculação de parâmetros e tentativas de injeção, páginas vazias/profundas e
reuso do pool, além de DTD contra o original. Os testes sem Oracle cobrem
validação, precedência, cancelamento, 413, erros sanitizados, serialização,
WSDL/OpenAPI e ações fora do escopo com servidor controlado.

Os casos de 49/50/51 linhas usam dados em memória e nunca escrevem no banco.
Não se afirma que uma página real de 51 notas foi observada: a carga atual não
fornece esse limite por prestador dentro do período aceito. Os testes Oracle
sem as variáveis exigidas ficam explicitamente ignorados; CI novo filtra a
categoria Oracle e não deve ser apresentado como integração real. CI hospedado
não foi executado, pois não houve publicação.

Ocorrências corrigidas: o restore/test restrito terminou sem diagnóstico útil
e foi repetido fora do sandbox; o primeiro build identificou ASP0016 no handler
REST, corrigido executando o IResult explicitamente. O WSDL inicialmente reduzido
perdeu elementos internos; o teste reforçado **falhou** (`Sequence contains no
elements`) antes da correção de inputXML/outputXML e soap:operation. A correção
mantém as novas asserções; não houve relaxamento de teste para obter aprovação.
Também foi substituído o regex de IM por comprimento/dígitos exatos e acrescentado
o caso de newline escapado. Artefato Python `__pycache__` gerado pelo teste foi
removido de forma restrita; arquivos de teste legados foram preservados.

## Paridade e divergências

Comparação externa com a imagem Docker:

```bash
python3 tools/verificar-consultar-nfse.py --legacy http://ORIGEM_LEGADO:8080 --modern http://ORIGEM_CONSULTA:8080 --report docs/evidencias/consultar-nfse-docker.json
```

As origens acima são marcadores para os IPs identificados por inspect na execução
histórica; esses endereços locais foram omitidos e não são configuração fixa. O [relatório do corpus](evidencias/consultar-nfse-docker.json)
compara status, content type e bytes exatos de cada resposta. Nenhuma normalização
XML, tolerância numérica ou exclusão de campos foi aplicada. Todos os 40 hashes
do legado continuam iguais ao baseline anterior à implementação.

| Caso de divergência | Classificação | Evidência / pendência |
| --- | --- | --- |
| Corpus de 40 consultas/erros/páginas/datas/estados | Igualdade na amostra | Comparação exata por caso; não certifica toda a população |
| DTD interno que fornece a IM por entidade | Decisão consciente de segurança | Teste Oracle: original retorna 3 notas; novo retorna E001/200 |
| Mensagem de falha interna/Oracle | Decisão consciente de confidencialidade | Novo retorna soap:Server/500 genérico; timeout simulado verificado; não houve interrupção do Oracle real |
| Corpo acima de 1 MiB | Decisão consciente de capacidade | Novo retorna 413 ProblemDetails; teste HTTP real local; legado não limita corpo |
| WSDL anuncia uma única operação e endereço interno | Decisão consciente de escopo | Testado schema/string/action; WSDL público integral preservado |
| Concorrência entre prestador/notas e entre páginas | Não certificado | Correção da revisão: novo usa SELECTs em autocommit e consistência por instrução; legado usa conexões separadas; sem experimento concorrente com escritas |
| Unicode não ASCII, offsets/DST, campos nulos/corrompidos, todos os erros Oracle | Não certificado | Sem alterações fiscais para criar fixtures; manter fora de alegação de paridade |

Não restou divergência inesperada nos casos executados. As divergências
conscientes e os casos não certificados impedem uma alegação de paridade total.

## Oracle e performance

Versão realmente observada: `Oracle AI Database 26ai Free Release 23.26.3.0.0`
(imagem `gvenzl/oracle-free:23-slim`). A documentação do repositório fala em
23ai e produção 19c; esta execução não foi em 19c.

Contagens iniciais e após os testes: NFSE **2000412**, CONTRIBUINTES **5030**,
GUIAS **120099**, RPS **3**, LOTES_RPS **3**. Esses agregados coincidem; não são
auditoria de imutabilidade de cada linha. O agendador interno do legado continuou
no seu estado preexistente. Nenhum DML/DDL, emissão, cancelamento ou sequence
NEXTVAL foi executado por esta verificação. Packages e trigger consultados
continuam VALID; last_ddl_time informado é anterior ao trabalho.

SELECT em `user_ind_columns` confirmou os índices descritos na ADR. A tentativa
de ler `v$sql` como NFSE_OWNER retornou ORA-00942. Sem alterar grants, a leitura
administrativa local de `v$sql`/`v$sql_plan` foi autorizada e executou apenas
SELECTs. Não houve EXPLAIN PLAN nem escrita em PLAN_TABLE.

Planos em cache observados: legado sem filtro `ctv420nyvy478` usa
`IX_NFSE_PRESTADOR_COMP`, SORT ORDER BY STOPKEY e COUNT STOPKEY; legado com filtro
`5qz58duhg647y` usa `IX_NFSE_TOMADOR_DT`. Novos `asfumgp5agn79` e `6vfs078wq475f`
mostraram respectivamente esses índices e uma ordenação externa explícita de RN.
A PK_NFSE atende o subselect da substituidora. A cardinalidade estimada 398 no
acesso histórico por prestador ilustra o risco, sem afirmar linhas/latência reais.
Os tempos cumulativos em cache incluem execuções anteriores e não foram usados
como benchmark. Nenhum índice, hint, estatística ou objeto Oracle foi alterado.

## Operação, riscos e rollback

Novo serviço em `consulta:8080`, usuário de container `1654`, sem porta publicada,
limite de 512 MiB e pool máximo 10. O Docker avisou que o kernel não suporta
limite de swap; limite de memória foi aplicado sem swap. O host não foi alterado.
Os GETs de live/ready/OpenAPI/WSDL/REST na imagem foram conferidos e resumidos
sem payload no [smoke Docker](evidencias/consultar-nfse-smoke-docker.json).

Origem pública continua `127.0.0.1:8080` → fachada → legado, sem dependência do
novo serviço. Oracle manteve sua porta local e seu volume, sem reinício/recriação.
Legado, fachada e três containers não relacionados foram preservados, conforme
registro histórico. Inventário inicial: 11 volumes e 17 redes. IDs, horários de
início e nomes locais de recursos foram omitidos da versão para commit.

Riscos residuais: usuário Oracle de desenvolvimento tem privilégios além de
SELECT; o serviço não autentica/autoriza contribuintes; dados e datas exigem
governança antes de exposição; paginação por offset não resolve alterações
concorrentes/carga. Não há sombra/roteamento dinâmico, tracing exportado,
Kubernetes, testes de carga, validação nativa 19c, produção ou ERP externo.

Rollback deste incremento: `docker compose stop consulta`. Retirar o bloco novo
do Compose se desejar remover sua integração. Não executar down/prune nem tocar
em volumes, Oracle ou outros containers. Nenhum rollback de dados é necessário.

## Estado final

**PASS para o recorte implementado e os testes executados; PARTIAL para paridade
integral.** Não há bloqueio à revisão local. A divergência de DTD e os limites
documentados impedem afirmar compatibilidade de todos os ERPs ou autorizar tráfego.

Resultado final: 76/76 testes novos, incluindo os 5 Oracle; 24/24 fachada;
16 sucessos e 1 ignorado preexistente no legado; 2/2 verificações Python.
Contadores e horários extraídos dos TRX estão em
[consultar-nfse-testes.json](evidencias/consultar-nfse-testes.json).

A imagem histórica foi conferida em execução, com o serviço consulta usando
usuário 1654, sem porta publicada. Smoke final: 5 GETs HTTP 200; WSDL inclui
inputXML/outputXML xsd:string e binding SOAPAction correto; consulta de um dia
retorna uma nota e preserva o zero inicial. Oracle, legado, fachada e os três
containers não relacionados conservaram IDs, StartedAt e portas, segundo o registro histórico. As mesmas listas de
11 volumes e 17 redes foram reconferidas no fim.

Corpus inicial na imagem final: 40/40 respostas exatamente iguais, exit 0.
Dois casos suplementares, capturados depois da implementação, não integram o
baseline inicial: overflow `85899346` reproduz as 3 linhas iniciais exatamente;
DTD retorna a divergência conscientemente esperada (3 notas no original, E001 no
novo). Ambos satisfazem suas expectativas, exit 0, sem divergência inesperada.
Evidência: [consultar-nfse-supplemental.json](evidencias/consultar-nfse-supplemental.json).

```bash
python3 -B tools/verificar-consultar-nfse.py --legacy http://ORIGEM_LEGADO:8080 --modern http://ORIGEM_CONSULTA:8080 --cases novo/tests/cases/consultar-nfse-supplemental.json --report docs/evidencias/consultar-nfse-supplemental.json
```

Git final: mesma branch e HEAD do preflight; index vazio. Modificados somente
README.md, docker-compose.yml e .github/workflows/ci.yml. Novos arquivos estão
em novo/, docs/adr/0002-consultar-nfse-independente.md, este relatório,
docs/evidencias/consultar-nfse-*.json e tools/verificar-consultar-nfse.py.
Nenhuma etapa de publicação foi iniciada.

Inventário de arquivos (3 modificados e 29 novos):

```text
M .github/workflows/ci.yml
M README.md
M docker-compose.yml
+ docs/adr/0002-consultar-nfse-independente.md
+ docs/verificacao-consultar-nfse.md
+ docs/evidencias/consultar-nfse-baseline.json
+ docs/evidencias/consultar-nfse-docker.json
+ docs/evidencias/consultar-nfse-smoke-docker.json
+ docs/evidencias/consultar-nfse-supplemental.json
+ docs/evidencias/consultar-nfse-testes.json
+ novo/Dockerfile
+ novo/README.md
+ novo/src/NotaCarioca.Consulta/NotaCarioca.Consulta.csproj
+ novo/src/NotaCarioca.Consulta/Models.cs
+ novo/src/NotaCarioca.Consulta/DocumentoFiscal.cs
+ novo/src/NotaCarioca.Consulta/ConsultarNfse.cs
+ novo/src/NotaCarioca.Consulta/OracleConsulta.cs
+ novo/src/NotaCarioca.Consulta/SoapAdapter.cs
+ novo/src/NotaCarioca.Consulta/RestApi.cs
+ novo/src/NotaCarioca.Consulta/Program.cs
+ novo/src/NotaCarioca.Consulta/openapi.json
+ novo/src/NotaCarioca.Consulta/ConsultarNfse.wsdl
+ novo/tests/NotaCarioca.Consulta.Testes/NotaCarioca.Consulta.Testes.csproj
+ novo/tests/NotaCarioca.Consulta.Testes/LegacyCharacterizationTests.cs
+ novo/tests/NotaCarioca.Consulta.Testes/ApplicationTests.cs
+ novo/tests/NotaCarioca.Consulta.Testes/SoapTests.cs
+ novo/tests/NotaCarioca.Consulta.Testes/HttpContractTests.cs
+ novo/tests/NotaCarioca.Consulta.Testes/HttpServer.cs
+ novo/tests/NotaCarioca.Consulta.Testes/OracleIntegrationTests.cs
+ novo/tests/cases/consultar-nfse.json
+ novo/tests/cases/consultar-nfse-supplemental.json
+ tools/verificar-consultar-nfse.py
```


Revalidação e correções posteriores: [revisão ConsultarNfse](revisao-consultar-nfse.md).
Os resultados e a imagem acima são históricos; não certificam os bytes corrigidos.
