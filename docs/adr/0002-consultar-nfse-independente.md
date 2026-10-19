# ADR 0002 — Primeira extração independente: ConsultarNfse

Status: implementada como serviço interno; ver [verificação](../verificacao-consultar-nfse.md).

## Contexto e escopo

O ponto de partida é `229f6278056d250d641d61044bec6ee46ddeea99`, com fachada
transparente. Este incremento extrai somente a consulta por prestador. Não altera
o roteamento público, o legado, o schema, triggers ou packages. Emissão,
cancelamento e consulta por tomador continuam fora do serviço novo.

`ConsultarNfse` não chama PL/SQL de negócio. O caminho original é
`WebServices/Program.LerInputXml` → `NfseWS.ConsultarNfse` →
`ConsultaNfseServico.ConsultarNfse` → `BuscarPrestador` / `LerDocumento` /
`LerPeriodo` / `LerPagina` → `NfseRepositorio.ConsultarPorPrestador` →
`XmlNfseBuilder.CompNfse` → escape do `outputXML` no envelope SOAP 1.1.
`LeitorLoteRps.Texto` usa XPath sem namespace e faz `Trim` do conteúdo.

## Decisão e arquitetura

Um único projeto ASP.NET Core `net8.0`, com arquivos pequenos para aplicação,
valores de domínio, leitura Oracle e adaptadores HTTP/SOAP. O projeto de produção
referencia apenas `Oracle.ManagedDataAccess.Core` 23.26.301, a mesma versão do
legado. Nenhuma referência aos assemblies legados; eles são oráculos dos testes.

O fluxo novo é REST/SOAP → `ConsultaSolicitada` → `OracleConsulta` abre conexão e
executa SELECTs em autocommit → `ConsultarNfse` valida e coordena a busca do
prestador e das notas → `PaginaNfse` → representação JSON/XML. Duas funções
assíncronas permitem testar a aplicação sem Oracle. Em produção as duas leituras
usam a mesma conexão, com consistência por instrução. Essa separação tem utilidade concreta sem uma
hierarquia de interfaces ou repositories.

Os valores da aplicação não conhecem XML, SOAP, códigos E001..E040 ou tipos ODP.NET.
`Periodo` contém limites explícitos e uma restrição opcional de competência;
`Janela` contém deslocamento e limite. Somente a borda SOAP transforma as
convenções antigas nesses valores. A aplicação mantém a ordem prestador →
documento → período, e não lê notas quando alguma validação falha.

Oracle continua fonte de verdade. Valores de ISS/base/alíquota não são recalculados.
Notas normais, canceladas e substituídas são retornadas. Nenhuma procedure de
emissão/cancelamento, sequência, DML, DDL ou `SELECT FOR UPDATE` foi adicionada.
Todos os valores SQL são parâmetros tipados com `BindByName=true`, inclusive o
documento como `Varchar2`. SQL dinâmico contém apenas dois fragmentos fixos de
predicados opcionais. O fetch materializa no máximo 50 notas; avança à 51ª linha apenas para indicar
a próxima página, sem decodificar suas colunas. Cancelamento HTTP chega ao ODP.NET; comandos têm timeout de
30 s. Não há retry. A conexão é descartada em sucesso/erro/cancelamento.

Não há exigência de snapshot entre a validação do prestador e a consulta de notas
neste incremento: o legado já executa essas leituras em conexões separadas.
O `SET TRANSACTION READ ONLY` isolado foi removido: sob autocommit ODP.NET não
sustenta a transação entre comandos e a alegação anterior de snapshot era indevida.
A investigação reproduzível está no teste Oracle
`ReadOnlyStatementUnderAutocommitDoesNotRetainTransaction`, com controle positivo
que desativa AutoCommit antes do SET e encerra com Rollback, sem DML/DDL.
Não se introduziu BeginTransaction seguido de SET TRANSACTION.
Documentação primária: [ODP.NET Command Auto-Commit](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featOraCommand.html).
Cada SELECT mantém sua consistência por instrução; não há snapshot entre comandos
nem entre páginas. Alterações concorrentes podem deslocar páginas, como no legado.

## Comportamentos encontrados e tratamento

| Regra observada no legado | Adaptador SOAP | REST v1 |
| --- | --- | --- |
| Busca por IM exata após Trim; exige ATIVO=S; CNPJ do prestador ignorado | Preserva | IM com exatamente 8 dígitos; prestador ausente/inativo = 404 |
| Primeiro valida prestador; depois documento, depois período | Preserva | Valida sintaxe antes do banco; inexistência = 404 |
| CPF/CNPJ apenas do primeiro Tomador; Cnpj tem preferência sobre Cpf dentro dele; remove todo caractere não dígito, inclusive letras; valida DVs | Preserva para dígitos ASCII | Só aceita texto canônico de 11/14 dígitos ASCII com DVs válidos |
| DateTime.TryParse com cultura invariável, não apenas ISO; trunca horário | Preserva | Apenas yyyy-MM-dd, sem horário/offset |
| Data final exclusiva; datas iguais consultam intervalo vazio | Preserva | Data final inclusiva; datas iguais consultam aquele dia |
| Diferença de até 31 dias aceita, abrangendo 32 datas no manual inclusivo | Preserva o limite/exclusividade reais | Até 31 dias inclusivos |
| SQL também exige COMPETENCIA <= DataFinal | Preserva | Sem restrição oculta de competência; intervalo de emissão é explícito |
| Ausente, texto, <1 ou fora do Int32 vira página 1 | Preserva | Página ausente = 1; inválida = 400; máximo 1000000 |
| Multiplicação de página usa Int32 unchecked; pode ficar negativa ou voltar a linhas iniciais | Preserva no adaptador, com teste de 85899346 | Aritmética Int64, sem wrap |
| Até 51 linhas; mostra 50; ProximaPagina baseado na linha extra; ORDER BY DT_EMISSAO, ID | Preserva; ordena também o RN externo explicitamente | Mesma ordem e tamanho; sem total global |
| Página vazia e nenhum resultado são E040 (HTTP 200), não ListaNfse vazia | Preserva | 200, itens=[], proximaPagina=false |
| Não exclui notas canceladas/substituídas | Preserva marcadores condicionais | Situação explícita, data e número de substituidora nullable |
| Documento original determina tag Cpf/Cnpj pelo comprimento, mas conversão numérica remove zeros do tomador | Preserva | Documento como string original, com zeros |
| Documento do prestador não passa pela conversão numérica | Preserva | String original |
| ISS é o valor persistido, inclusive diferenças de arredondamento em relação ao PL/SQL | Preserva | Preserva |
| Valores com 2 casas, alíquota 0.####, datas sem offset/fração, competência no primeiro dia, IssRetido 1/2 | Preserva bytes/escape/ordem | JSON decimal/bool/enum; datas locais sem offset e competência como date |
| Nome da raiz é sensível a prefixo; XPath ignora elementos em namespace padrão | Preserva E001/E010 observados | Não aplicável |
| Host escolhe ação pelo último segmento de SOAPAction; encontra primeiro inputXML por LocalName, sem validar wrapper/namespace | Preserva para ConsultarNfse | Não aplicável |
| XML interno inválido = E001/200; envelope inválido = soap:Client/500 | Preserva | Não aplicável |

Mensagens e correções E001/E010/E020/E030/E040 são serializadas com os mesmos
textos, ordem, escape e envelope. Os casos existentes `04` e `04e` são dados
representativos, não prova de toda a população.

## Divergências deliberadas e limites de compatibilidade

1. **DTD interno (inclusive DOCTYPE sem entidades):** o `XmlDocument.LoadXml` legado aceita entidades internas.
   O novo proíbe DTD e resolver externo; retorna E001. Medido contra o legado:
   um DTD que fornece a IM retorna 3 notas no original e E001 no novo. Classificação:
   decisão consciente de segurança, incompatibilidade que impede declarar paridade total.
2. **Referência a caractere inválido:** o leitor antigo aceita `&#1;` no XML interno;
   o XmlReader novo verifica caracteres XML 1.0 e retorna E001/200. Política
   deliberada de validação estrita, sem desativar CheckCharacters.
3. **Profundidade:** antes do DOM e de InnerText, uma passagem sequencial rejeita
   mais de 64 elementos aninhados (raiz incluída). XML interno excedente retorna
   E001/200; envelope excedente mantém soap:Client/500. O InnerText pode percorrer
   descendentes recursivamente; o limite de bytes isolado não limita a pilha.
   Testes locais cobrem 64, 65 e 4096 níveis sem testar falha fatal no legado.
4. **Falhas internas/Oracle:** o legado expõe `ex.Message` em `soap:Server`.
   O novo usa texto genérico. Decisão consciente para não divulgar SQL/conexões/dados.
   Testado com timeout controlado; não foi interrompido o Oracle real para produzir
   essa falha. Status SOAP continua 500; REST usa 503 para Oracle/timeout e 500
   para falha interna. Sem equivalência textual prometida nesse ramo.
5. **Limites de entrada:** novo limita corpo a 1 MiB e XML a 1 Mi caracteres.
   O legado não impõe o limite de corpo. Consulta não necessita lotes grandes.
   HTTP 413 é documentado; não usar o serviço para encaminhar outras operações.
6. **WSDL interno:** anuncia só ConsultarNfse e endereço `http://consulta:8080/NfseWS.asmx`.
   Mantém Request/Response, inputXML/outputXML xsd:string, SOAPAction e namespace.
   O WSDL público continua integral e pertence à fachada/legado. Não fornecer o
   WSDL interno a ERPs como substituto público nesta fase.

Pendências: documentos Unicode não ASCII podem ter comportamento distinto nas
conversões VB.NET; linhas com campos numéricos/datas/documentos nulos ou
corrompidos podem produzir falhas diferentes. Essas entradas não foram usadas
para alterar o banco. Datas com offset dependem do timezone do host; Compose
configura America/Sao_Paulo, mas a paridade de offsets/DST históricos não foi
certificada. Interleavings concorrentes entre as duas leituras e entre páginas,
falhas reais do Oracle, clientes ERP externos e amostras além dos casos descritos
continuam sem certificação. Nenhum desses limites é resolvido enfraquecendo testes.

## Performance e operação

Índices relevantes existentes: `UK_CONTRIB_IM(IM)`, `UK_NFSE_NUMERO(PRESTADOR_ID,NUMERO)`,
`IX_NFSE_PRESTADOR_COMP(PRESTADOR_ID,COMPETENCIA)`,
`IX_NFSE_TOMADOR_DT(TOMADOR_DOCUMENTO,DT_EMISSAO)` e `PK_NFSE(ID)`.
Não há índice composto `(PRESTADOR_ID,DT_EMISSAO,ID)`. O predicado de competência
do SOAP pode ler histórico e ordenar antes do stopkey. REST sem esse predicado
pode escolher outro plano. O subselect da substituidora usa a PK. Offset profundo
continua custoso; não há COUNT total nem hint FULL/TRUNC nas datas nesta operação.
O hint FULL e TRUNC estão na consulta por tomador, fora deste incremento.
Nenhum índice foi criado e nenhum EXPLAIN PLAN que escreva PLAN_TABLE foi executado.
Planos observados por SELECT em caches são evidência local, não benchmark 19c.

Compose acrescenta `consulta` com `expose: 8080`, limite de 512 MiB, pool máximo
10 e usuário de container sem root. Não publica porta nem referencia o serviço
na fachada. Health live é local; readiness consulta `SELECT 1 FROM DUAL`.
Logs próprios suprimem conteúdo de exceções/dados pessoais e queries HTTP;
respostas têm `Cache-Control: no-store`. Não há autenticação/autorização nova:
o serviço deve permanecer interno. Traces distribuídos/exportador, métricas,
modo sombra, flags, Kubernetes e rollout público não fazem parte desta entrega.

Usa o usuário de desenvolvimento existente, que tem permissões de proprietário;
SQL fixo não substitui um usuário com somente SELECT. Antes de
qualquer rollout, provisionar credencial restrita fora deste incremento, sem
alterar tabelas/packages. Não introduzir credenciais de produção no repositório.

## Alternativas consideradas

- Reutilizar `ConsultaNfseServico` seria rápido, mas manteria dependência de XML,
  DataTable, convenções e acesso Oracle do legado. Rejeitado para uma extração independente.
- EF Core/Dapper, MediatR, CQRS, repository/interface por entidade e múltiplos
  projetos acrescentariam dependências e abstrações sem valor nesta leitura.
  ADO.NET assíncrono e funções de leitura tornam a dependência verificável.
- SoapCore/WCF ou cliente gerado poderiam reserializar wrappers/strings e esconder
  comportamentos do host. Adaptador explícito é pequeno, testável byte a byte e
  atende uma operação. Reavaliar se o contrato crescer.
- Corrigir a exclusividade, zeros e paginação no SOAP quebraria compatibilidade.
  As correções pertencem ao contrato REST documentado.
- CDC/cache/banco novo introduziria defasagem e operação desnecessárias. Oracle
  segue proprietário e autoridade; este passo não replica dados.
- Keyset pagination e índice próprio seriam caminhos de performance, mas mudam
  contrato/infraestrutura. Exigem evidência de carga e autorização posterior.

## Rollback

Nenhuma flag precisa ser alterada: a porta pública segue fachada → legado.
Parar somente `docker compose stop consulta`. Para remover a integração do
arquivo Compose, retirar o bloco `consulta`; o código pode permanecer inativo.
Não executar down, prune, down -v, recriar Oracle ou remover volumes. Nenhum
rollback de dados é necessário. Confirmar health e o hash da consulta pública
existente. Uma futura adoção pública exigirá gates próprios de paridade,
autorização, segurança, carga e reversão; não está autorizada por esta ADR.
