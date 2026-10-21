# Observabilidade mínima — fachada e consulta

Implementação local em `feat/fachada-soap-legado`, sobre
`63ee7a120e14eb5dd1a5ef19dd5fe124815c518d`. O commit adicional ao SHA anteriormente
esperado contém somente AGENTS.md; suas regras de simplicidade foram preservadas.

## Implementação

OpenTelemetry SDK 1.20.0, integração ASP.NET Core e exportador OTLP/gRPC nos dois
executáveis .NET 8. `Telemetry.cs` em cada aplicação configura os instrumentos
nativos ActivitySource/Meter e o SDK, sem projeto compartilhado, AppHost ou nova
camada. Os três pacotes diretos atendem respectivamente ao ciclo de vida do SDK,
exportação e tracing de entrada. As versões iniciais 1.15.0 foram rejeitadas pelo
restore por advisories; o gate de vulnerabilidades não foi desabilitado.

- Spans de entrada SOAP, HTTP de saída para legado/novo, trabalho shadow e dois
  SELECTs do serviço novo (`Oracle SELECT prestador`, `Oracle SELECT nfse`). Os
  spans HTTP incluem envio e consumo da resposta. Os spans SELECT incluem leitura
  do resultado, sem SQL ou parâmetros.
- W3C traceparent/tracestate: contexto recebido é propagado com novo SpanId de
  saída; a fila shadow carrega ActivityContext. Baggage não é encaminhado.
- `nfse.soap.requests`: contador por `soap.operation` e `routing.mode` efetivo.
- `nfse.soap.duration`: histograma em segundos, buckets de 5 ms até 660 s, mesmos labels, até o término do
  handler incluindo streaming; não inclui conclusão assíncrona do shadow.
- `nfse.shadow.observations`: contador por `shadow.result`, com `parity`,
  `expected-divergence`, `unexpected-divergence`, `dropped`. Saturação, captura
  incompleta, limites, timeout, falha de transporte e parada são descartes, nunca
  paridade ou divergência funcional. As quatro séries são inicializadas em zero.
- Operações pertencem à lista fixa do WSDL, com fallback `other`. Modos são
  LEGACY/NEW/SHADOW; não há labels de contribuinte, inscrição, URL ou TraceId.
- Logs JSON no stdout e logs OTLP correlacionados automaticamente com TraceId e
  SpanId. O log de conclusão também contém TraceId explícito. Scopes automáticos
  de request não são exportados/impressos: o caminho REST pode conter inscrição.
- Um processador padrão do SDK restringe atributos de spans a uma lista explícita
  e remove URLs, queries e texto de erro automático antes de exportar. Não há
  captura de SOAP, XML fiscal, parâmetros Oracle, documentos, inscrições,
  Authorization ou credenciais.

O backend é o Aspire Dashboard standalone `9.5.0`, com limite de 512 MiB,
visualização de traces/métricas/logs OTLP e armazenamento em memória. Há somente
um container adicional e nenhum volume adicional. A interface está publicada em
`127.0.0.1:18888`; OTLP fica na rede interna do Compose. O modo anônimo serve à
demonstração local. Não há dependência de saúde do backend no processamento SOAP.
Exportação usa as filas e os leitores padrão do SDK, com timeout de 1 segundo;
falhas de entrega podem perder observações, sem replay de SOAP.

Referência do modo standalone e configuração:
https://aspire.dev/dashboard/standalone/ . Foi usada uma tag cuja disponibilidade
foi confirmada no registry, em vez da tag 13.6.0 indisponível no ambiente.

## Reprodução com o volume existente

Não executar `down -v`, inicializadores de dados ou o script de exemplos em lote.
Para o ambiente existente, iniciar os serviços preservados e aplicar somente as
imagens instrumentadas:

```sh
docker compose start oracle webservices
docker compose build fachada consulta
docker compose up -d --no-deps observabilidade fachada consulta
python3 tools/verificar-observabilidade.py
```

O script faz somente ConsultarNfse, alterna temporariamente LEGACY/NEW/SHADOW e
kill switch, compara bytes e restaura o arquivo de roteamento em `finally`.
Executar em ambiente local sem alterações concorrentes no arquivo de roteamento.
A variante com referência de caractere inválido observa uma divergência já
conhecida; não executa escrita fiscal. Os TraceIds impressos não são labels.

Abrir http://127.0.0.1:18888 :

1. Em Traces, selecionar o TraceId NEW impresso pelo script. O waterfall mostra
   fachada SOAP → HTTP modern → consulta SOAP → dois SELECTs Oracle.
2. No SHADOW, a consulta nova está sob o span de trabalho shadow no mesmo trace;
   o tempo total desse trace pode ultrapassar a latência da resposta ao cliente.
3. Em Metrics, selecionar `nfse-fachada`, escolher cada instrumento na árvore e
   usar Graph/Table e os filtros por modo/operação/resultado. Aguardar ao menos
   um intervalo de exportação de 5 segundos.
4. Em Structured, abrir os logs com o mesmo TraceId e os resultados de comparação.

## Evidência executada em 2026-10-21

Cinco consultas da demonstração final retornaram HTTP 200, 4.361 bytes e SHA-256
`926d073e52cc26d9b3341075bf275567c40b9121468a94fe2b444357aef7f735`.
O roteamento foi restaurado ao SHA-256
`7dab551de799ca909bdc983e0b032082a31f6c10be14a62229b4a81b29d1ccb4`.

| Caminho | TraceId final |
| --- | --- |
| LEGACY | `6e0469a97ee44274a110725ebbf69e56` |
| NEW | `418aa530ce2a4ace9c23299a855841ec` |
| SHADOW paridade | `f980f31efd894b208c578e3629961e53` |
| SHADOW divergência XML esperada | `8ce78dec5d9748ca92195744cf3a6b8a` |
| KillSwitch → LEGACY | `1313af50794346a8be071d88b59041a8` |

A interface real foi verificada em Chrome headless/Playwright. O trace NEW mostra
5 spans em 2 recursos; duração exibida de 0,14 s, SELECT prestador 2,96 ms e
SELECT nfse 2,43 ms. O histograma final exibiu P50 de 0,075 s e P90/P99 de
0,5 s, estimativas pelos buckets para apenas cinco requisições. São tempos locais de uma consulta, sem conclusão de desempenho.
O SHADOW de paridade contém 7 spans; a rejeição XML no novo serviço não abre
SELECTs. O painel recebeu os três instrumentos e seus labels de baixa cardinalidade.
Logs reais registraram parity e expected-strict-xml associados aos traces.

Duas consultas adicionais com o container de observabilidade parado retornaram
HTTP 200, mesmos bytes/hash. O backend foi reiniciado e os traces finais gerados
depois, pois seus dados são voláteis. O contador final de requests é 5, após recriar a fachada: LEGACY 2, NEW 1,
SHADOW 2. A execução anterior acumulou 7, incluindo as duas consultas de
indisponibilidade. O contador final de shadow é 2: paridade 1, divergência
esperada 1, divergência inesperada 0 e descartes 0. As comparações reais
produziram paridade e divergência esperada; divergência inesperada e saturação
foram verificadas com upstreams controlados no teste, sem produzir esses eventos
artificialmente contra Oracle.

Regressões, .NET SDK 8.0.132, Release:

| Execução | Resultado |
| --- | --- |
| `dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --no-restore` | 51 PASS; 1 Oracle ignorado nesta execução |
| `dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --no-restore` | 86 PASS; 7 Oracle ignorados nesta execução |
| Mesmos projetos, `--no-build --no-restore --filter TestCategory=Oracle`, endpoints/configuração atuais explícitos | 1 + 7 PASS; zero skips |
| Teste `TelemetryPreservesW3CContextAcrossLegacyNewAndShadowWithBoundedLabels`, após ajuste final | PASS; contexto, métricas e os quatro resultados shadow, incluindo saturação determinística |

Os projetos compilaram sem warnings/erros com as dependências finais. O ambiente
precisou de acesso autorizado fora do sandbox para Docker, rede NuGet e sockets
de testes/navegador. Não foram alteradas configurações Docker ou regras de rede.
Evidências operacionais, TRX e screenshots estão em `/tmp/nfse-observability/`;
nenhum payload/resposta fiscal ou credencial foi incluído neste documento.

Preservação: Oracle e webservices mantiveram IDs, criação e montagens. Os três
containers `dashboard-current-preview-*` mantiveram inclusive StartedAt. Metadados
do volume Oracle iguais antes/depois. Consultas em transação READ ONLY verificaram
contagens iguais: NFSE 2.000.412, contribuintes 5.030, RPS 3, lotes 3 e guias 120.099;
somas de IDs/valores/ISS e hash agregado de situação de NFSE também iguais. Não se
trata de comparação integral do banco. Somente fachada/consulta foram recriados;
Oracle e legado existentes foram iniciados, sem recriação ou alteração de objetos.

## Limites operacionais

- Legado recebe W3C trace context e há span HTTP da fachada, mas seu interior e
  seu acesso Oracle não estão instrumentados. Não se afirma tracing Oracle no
  caminho LEGACY nem cobertura interna completa do legado.
- Spans Oracle são manuais nas duas consultas SELECT do serviço novo, não
  instrumentação geral do driver; abertura do pool, health check, Oracle interno
  e falhas específicas do banco não tiveram cobertura de spans validada.
- Não foram executadas operações fiscais de escrita no ambiente real. Testes de
  roteamento de escritas usam upstreams controlados e continuam sem retry/fallback.
- O dashboard é local, anônimo e volátil; não há retenção durável, alertas, SLA,
  autenticação de produção ou benchmark de capacidade. Perda de exportação é
  independente da métrica de descartes do shadow, que mede observações de negócio.
- Tracing de entrada está limitado ao endpoint SOAP. REST com identificadores
  no caminho não exporta spans HTTP de entrada. Spans SELECT também podem existir
  quando a consulta nova é invocada por REST, sem cobertura distribuída REST validada.
- Contadores reiniciam com o processo; sampling respeita o contexto upstream.
  Identificação da operação vem de SOAPAction normalizada, sem inspeção extra do corpo.

Na demonstração inicial, nenhum stage, commit, push ou PR foi realizado.

Verificação focada para o fechamento local: os dois `assert` operacionais foram
substituídos por validações explícitas com `SystemExit`. O teste padrão
`python3 -m unittest discover -s tools/tests -v` passou (3 testes); a regressão
de observabilidade cobre oito cenários: sucesso e respostas incorretas em NEW,
SHADOW e kill switch, em Python normal e `-O`. Confirma as cinco chamadas no
sucesso, saída 1 nas divergências e restauração exata do roteamento em todos os
cenários, sem rede ou configuração real.
O verificador corrigido também passou no Compose existente em ambos os modos:
cinco consultas por execução, HTTP 200, mesmos bytes/hash documentados acima e
restauração ao mesmo hash de roteamento. Estado final: `KillSwitch=true`,
`Mode=legado`, `Percentage=0`, `Providers=[]`. As regressões Release foram
reexecutadas: fachada 51 PASS/1 ignorado e consulta 86 PASS/7 ignorados; os testes
Oracle condicionais não foram habilitados nesta reexecução. Nenhum código de
produção foi alterado durante esta correção do verificador.

## Arquivos deste incremento

- `docker-compose.yml`.
- `fachada/src/NotaCarioca.Fachada/Telemetry.cs`, `Program.cs`, `LegacyProxy.cs`,
  `SoapRouting.cs`, `ShadowConsultation.cs`, `NotaCarioca.Fachada.csproj`.
- `fachada/tests/NotaCarioca.Fachada.Testes/RoutingTests.cs`.
- `novo/src/NotaCarioca.Consulta/Telemetry.cs`, `Program.cs`, `OracleConsulta.cs`,
  `NotaCarioca.Consulta.csproj`.
- `tools/verificar-observabilidade.py`, `tools/tests/test_verificar_observabilidade.py`
  e `docs/observabilidade.md` (15 arquivos ao todo).
