# Consulta independente de NFS-e (.NET 8)

Somente `ConsultarNfse`, com aplicação independente do legado, leitura Oracle,
REST v1 e adaptador SOAP 1.1. Decisões e divergências:
[ADR 0002](../docs/adr/0002-consultar-nfse-independente.md).
Resultados locais: [verificação](../docs/verificacao-consultar-nfse.md).

## Executar sem mudar o tráfego público

Com o Oracle local já saudável:

```bash
docker compose build consulta
docker compose up -d --no-deps consulta
```

`consulta:8080` fica somente na rede interna do Compose. Não há porta publicada.
`localhost:8080` continua sendo a fachada; o padrão é legado e a adoção de
ConsultarNfse é controlada pelo [roteamento runtime](../fachada/README.md). Não
recriar os serviços existentes para testar a extração. Não executar `just lote`,
scripts de lotes, runner geral de exemplos ou operações de cancelamento.

Para desenvolvimento fora do Docker, configure a conexão local existente:

```bash
# Defina NFSE_CONNECTION_STRING com a conexão da réplica local descrita no Compose.
# Fora do Docker, use Data Source=127.0.0.1:1521/FREEPDB1.
TZ=America/Sao_Paulo dotnet run --project novo/src/NotaCarioca.Consulta --urls http://127.0.0.1:8081
```

As credenciais do Compose pertencem à réplica de desenvolvimento. Não usar em
produção. A configuração é obrigatória; não há conexão implícita de fallback.

## Contratos

| Método e endpoint interno | Comportamento |
| --- | --- |
| GET `/api/v1/prestadores/{inscricaoMunicipal}/nfse?dataInicial=2026-10-01&dataFinal=2026-10-31&pagina=1` | JSON, 50 notas/página, ordem de emissão e ID |
| POST `/NfseWS.asmx` | Apenas SOAPAction `http://notacarioca.rio.gov.br/ConsultarNfse` |
| GET `/NfseWS.asmx?wsdl` | Contrato SOAP somente da operação extraída |
| GET `/openapi/v1.json` | OpenAPI 3.0.3 da REST v1, incluindo schemas e erros |
| GET `/health/live` | Processo ativo |
| GET `/health/ready` | Conexão Oracle e SELECT 1 |

REST: IM com 8 dígitos; datas estritas `yyyy-MM-dd`; até 31 dias **inclusivos**;
`dataFinal` anterior a 9999-12-31; `pagina` 1..1000000, padrão 1;
`documentoTomador` opcional, CPF/CNPJ com DVs válidos e 11/14 dígitos sem pontuação.
Parâmetros desconhecidos/repetidos são erro. Datas/horas nas notas são locais,
sem offset (Oracle DATE não armazena timezone). Documento preserva zeros à
esquerda; situação é enum textual; ISS é o valor do banco, sem recálculo.

Resposta: `{ "itens": [...], "pagina": 1, "tamanhoPagina": 50,
"proximaPagina": false }`. Página vazia é 200. Não existe total global nem
snapshot entre comandos ou páginas; os SELECTs usam autocommit e consistência por instrução.

Erros REST usam `application/problem+json` com `type`, `title`, `status` e
`codigo`: 400 para entrada inválida, 404 para prestador ausente/inativo,
503 para Oracle/timeout, 500 para falha interna. O limite de corpo é 1 MiB
(413, também em requisições SOAP); não há detalhes de SQL/conexão na resposta.
Sem autenticação/autorização neste incremento: manter acesso interno.

SOAP preserva as convenções antigas, inclusive fim exclusivo, E040/200 para
página vazia, CNPJ de prestador ignorado, paginação Int32 e zeros suprimidos no
documento do tomador. Use os exemplos `04-consultar-nfse.xml` e `04e-*`.
Não usar o WSDL interno como contrato público integral. DTD (mesmo sem entidades),
referências a caracteres XML inválidos e XML com mais de 64 níveis são rejeitados
(E001 no XML interno; soap:Client/500 no envelope).
Falhas internas são genéricas e o corpo tem limite: divergências conscientes
descritas na ADR. Não existe implementação de emissão/cancelamento neste serviço.

## Verificar

```bash
dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --filter 'TestCategory!=Oracle'
```

Integração real, sem DML/DDL nem chamada fiscal de escrita:

```bash
export NFSE_TEST_CONNECTION_STRING="$NFSE_CONNECTION_STRING"
export NFSE_LEGACY_BASE_URL=http://127.0.0.1:8080
dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --filter 'TestCategory=Oracle'
```

O GitHub Actions executa somente os testes fora da categoria `Oracle`; não
provisiona Oracle. Resultados de integração documentados são locais.

Os 7 testes `Oracle` exigem essas variáveis. Sem configuração ficam ignorados
com motivo explícito; isso não conta como integração aprovada. A suíte compara
os 40 casos com o baseline capturado antes da implementação e com o host
original, exercita Oracle parametrizado, inclusão do dia final REST e reuso do
pool. Dados esperados são os seeds existentes. A validação de 49/50/51 linhas
é controlada em memória; a carga existente não fornece essas páginas por
prestador no intervalo permitido. Nenhum dado fiscal foi criado para o teste.

Verificador externo (também pode comparar a imagem Docker). Use as origens
internas atuais acessíveis ao host, consultadas por `docker inspect`:

```bash
python3 tools/verificar-consultar-nfse.py --legacy http://ORIGEM_LEGADO:8080 --modern http://ORIGEM_CONSULTA:8080 --report /tmp/paridade-consultar-nfse.json
```

O verificador fixa a ação `ConsultarNfse`; grava hashes, status e contagens,
sem payloads ou documentos pessoais. `--modern` é opcional para caracterização.
As respostas são comparadas byte a byte, junto de status/content type; nenhuma
normalização ou remoção de campo. Baseline e comparação não certificam toda a
população, todos os erros ou Oracle 19c de produção.

`--cases novo/tests/cases/consultar-nfse-supplemental.json` executa os dois casos
suplementares de overflow e DTD. A divergência DTD tem expectativa explícita
também para o serviço novo; qualquer resultado diferente falha a verificação.

Rollback: ativar primeiro o kill switch da fachada e conferir a consulta legada;
então `docker compose stop consulta`. Sem flag válida, a fachada usa o legado.

Correções da revisão independente e evidência atual: [relatório da revisão](../docs/revisao-consultar-nfse.md).
