# Correções da revisão independente — ConsultarNfse

Data: 2026-10-19. Branch `feat/fachada-soap-legado`, HEAD
`229f6278056d250d641d61044bec6ee46ddeea99`, conferidos antes/depois.
Esta execução revisou e corrigiu o incremento não commitado preexistente.
Não houve stage, commit, push, PR, alteração de roteamento, DML/DDL ou chamada
fiscal de escrita. Nenhum container foi parado, recriado ou atualizado.

## Achados e correções

1. **Tomador confirmado.** O legado chama `SelectSingleNode("Tomador")` antes de
   `LerDocumento`; dentro desse nó busca Cnpj e, se vazio após Trim, Cpf.
   O XPath anterior do novo serviço podia encontrar Cnpj em um segundo Tomador.
   Agora o adaptador seleciona um único Tomador antes de aplicar essa precedência.
   Regressões cobrem primeiro CPF, primeiro Tomador vazio, CNPJ vazio com CPF,
   e CNPJ/CPF simultâneos. Dois envelopes completos foram comparados contra o
   legado: primeiro CPF seguido de CNPJ retorna E040; primeiro vazio seguido de
   CNPJ retorna as 3 notas sem filtro. Ambos são byte a byte idênticos ao original.
   **Limitação de proveniência:** os dois payloads literais do parecer Claude não
   foram fornecidos nem encontrados no repositório. Os casos foram reconstruídos
   a partir do achado; não se afirma identidade literal com os anexos do parecer.
2. **READ ONLY ineficaz confirmado.** ODP.NET Core 23.26.301, autocommit padrão:
   após SET e SELECT, `AutoCommit=True`, `IsInTransaction=False`, ID local vazio.
   Controle positivo na mesma conexão: AutoCommit desativado antes do SET;
   `IsInTransaction=True` e ID local presente; Rollback explícito ao fim.
   O probe usa `SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL`, sem
   solicitar criação de transação. Não executa DML/DDL/SELECT FOR UPDATE.
   A documentação [Oracle Command Auto-Commit](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featOraCommand.html)
   confirma o commit automático fora de transação. Como o legado já usa leituras
   separadas e este incremento não exige snapshot entre consultas, removeu-se o
   SET da produção. ADR, comentário da aplicação e verificação foram corrigidos.
   Permanecem SELECTs parametrizados e consistência por instrução; nenhuma
   garantia de snapshot entre comandos/páginas. Não foi introduzido BeginTransaction.
3. **Divergências XML confirmadas e deliberadas.** DOCTYPE sem entidades e
   referência `&#1;` em elemento ignorado são aceitos pelo parser antigo e
   retornam 3 notas. O novo retorna E001/200 em ambos. Mantidos DtdProcessing.Prohibit,
   XmlResolver=null e verificação de caracteres XML 1.0. Casos unitários e contra
   o host original registram a classificação de segurança explicitamente.
4. **Profundidade: risco por inspeção.** InnerText chama AppendChildText, que se
   chama recursivamente nos descendentes ([fonte .NET](https://source.dot.net/System.Private.Xml/System/Xml/Dom/XmlNode.cs.html));
   limite de bytes não limita a profundidade desse percurso. Não se tentou provocar
   StackOverflow no legado nem nos containers compartilhados. Uma passagem
   XmlReader sequencial rejeita mais de 64 elementos aninhados, incluindo a raiz,
   antes de construir o DOM e de acessar InnerText. XML interno excedente retorna
   E001/200; envelope excedente retorna soap:Client/500. Testes locais de 64, 65 e
   4096 níveis, com não invocação da aplicação nos rejeitados, e teste HTTP dos
   envelopes completos passaram. A passagem também exige envelope integralmente
   bem formado, inclusive conteúdo posterior a inputXML; é validação estrita.
5. **Linha extra confirmada.** Antes eram decodificadas até 51 notas. Agora a função
   de leitura retorna PaginaNfse com até 50 itens e um booleano calculado por um
   único ReadAsync adicional; nenhuma coluna da linha extra é decodificada pela
   aplicação. Teste focal cobre 0/49/50/51/52 linhas e falha se a decodificação
   ultrapassar 50. Não se promete eliminar prefetch do driver ou trabalho do banco.
   Não foram inseridas 51 notas reais para testar esse cenário.

## Evidência fresca e comandos

.NET SDK 8.0.131; runtime 8.0.31; ODP.NET Core 23.26.301.
Conexão local: réplica existente em `127.0.0.1:1521/FREEPDB1`, usuário de
desenvolvimento já configurado no Compose; nenhum grant foi modificado.
Para reproduzir, definir NFSE_TEST_CONNECTION_STRING com essa conexão existente.
As variáveis são consumidas pelos testes; a aplicação temporária usou
NFSE_CONNECTION_STRING com a mesma origem, sem credenciais novas.

```bash
NFSE_LEGACY_BASE_URL=http://127.0.0.1:8080 TZ=America/Sao_Paulo DOTNET_PROCESSOR_COUNT=2 \
  dotnet test novo/tests/NotaCarioca.Consulta.Testes -c Release --no-restore -m:1 \
  --logger 'trx;LogFileName=review-final-http-depth.trx' --results-directory /tmp/nfse-review

dotnet test legado/tests/NotaCarioca.Testes -c Release -f net8.0 --no-restore \
  --logger 'trx;LogFileName=review-legacy.trx' --results-directory /tmp/nfse-review
dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --no-restore \
  --logger 'trx;LogFileName=review-facade.trx' --results-directory /tmp/nfse-review
python3 -B -m unittest discover -s tools/tests -v
docker compose config --quiet
git diff --check
git diff --cached --stat
git status --short
```

| Validação | Resultado |
| --- | --- |
| Serviço novo, suíte final completa | 93/93; zero falhas/ignorados; exit 0 |
| Integração Oracle incluída acima | 7/7, banco real e host original |
| Fachada HTTP/SOAP | 24/24; exit 0 |
| Legado net8.0 | 16 aprovados, 1 ignorado preexistente Parse_MesPorExtenso; exit 0 |
| Verificador Python | 2/2; exit 0 |
| Compose / diff whitespace | exit 0 / exit 0 |

A tentativa inicial dentro do sandbox falhou com SocketException/Permission
Denied do MSBuild; foi repetida fora dele. A primeira suíte completa passou
91 casos e falhou 1 porque o build já iniciado carregara o csproj antes da
inclusão do JSON de revisão. Após rebuild passou 92/92; com o teste HTTP de
profundidade incluído, a suíte final passou 93/93. Nenhuma asserção foi relaxada.
Contadores, nomes dos testes, hashes dos fontes e observação transacional:
[evidência dos testes](evidencias/consultar-nfse-review-testes.json).
TRXs originais estão em `/tmp/nfse-review`; não são arquivos versionados.

A paridade externa usou o código corrigido compilado executado temporariamente
em `127.0.0.1:18081`, encerrado depois dos testes. O container consulta existente
não foi atualizado; sua imagem antiga não certifica as correções.

```bash
# Com NFSE_CONNECTION_STRING definido e TZ=America/Sao_Paulo:
DOTNET_PROCESSOR_COUNT=2 dotnet novo/src/NotaCarioca.Consulta/bin/Release/net8.0/NotaCarioca.Consulta.dll --urls http://127.0.0.1:18081
python3 -B tools/verificar-consultar-nfse.py --legacy http://127.0.0.1:8080 --modern http://127.0.0.1:18081 --report docs/evidencias/consultar-nfse-review-corpus-original.json
python3 -B tools/verificar-consultar-nfse.py --legacy http://127.0.0.1:8080 --modern http://127.0.0.1:18081 --cases novo/tests/cases/consultar-nfse-supplemental.json --report docs/evidencias/consultar-nfse-review-supplemental.json
python3 -B tools/verificar-consultar-nfse.py --legacy http://127.0.0.1:8080 --modern http://127.0.0.1:18081 --cases novo/tests/cases/consultar-nfse-review.json --report docs/evidencias/consultar-nfse-review-regressions.json
```

| Corpus | Casos | Respostas completas idênticas | Divergências esperadas | Falhas / exit |
| --- | ---: | ---: | ---: | --- |
| Original imutável | 40 | 40 | 0 | 0 / 0 |
| Suplementar preexistente | 2 | 1 (overflow) | 1 (DTD com entidade) | 0 / 0 |
| Revisão | 4 | 2 (Tomador) | 2 (DOCTYPE vazio e caractere inválido) | 0 / 0 |

Comparação inclui bytes completos, status e Content-Type, sem normalização.
A suíte Oracle também reconferiu os hashes dos 40 originais contra o baseline
preexistente. Nenhuma divergência funcional inesperada no corpus executado.
Os JSONs preservam hashes e agregados, sem respostas/payloads fiscais.

## Arquivos e estado final

Alterados nesta revisão: SoapAdapter.cs, OracleConsulta.cs, ConsultarNfse.cs e
NotaCarioca.Consulta.csproj; ApplicationTests.cs, OracleIntegrationTests.cs e
NotaCarioca.Consulta.Testes.csproj; ADR 0002, novo/README.md e
verificacao-consultar-nfse.md. Adicionados: ReviewRegressionTests.cs, corpus
consultar-nfse-review.json, este relatório e cinco JSONs review de evidências.
Sem dependências ou camadas novas. Código e testes do legado/fachada preservados.
README.md da raiz, CI e Compose mantêm as alterações herdadas, sem edição nesta
revisão. Evidências históricas JSON e baseline não foram reescritos.

O diff tracked herdado contém 3 arquivos, 23 inserções e 2 remoções. Os fontes
novos continuam untracked e não aparecem nesse diff. O index permanece vazio.
Inventário final: [preservação](evidencias/consultar-nfse-review-preservacao.json).
Os 7 containers seguem running; IDs/StartedAt de Oracle/legado/consulta conferem
com o preflight. Fachada mantém seu ID e porta 127.0.0.1:8080. As listas de 11
volumes e 17 redes coincidem com as iniciais. Não houve comandos de mutação Docker.

## Limites e classificação

PASS para as correções implementadas e as validações executadas. PARTIAL para
o requisito literal de usar os dois payloads originais do parecer, ausentes no
material fornecido; os dois cenários reconstruídos estão aprovados. A profundidade
é mitigada preventivamente por inspeção; não foi reproduzida terminação fatal.
Não se afirma paridade total: DTD, caracteres inválidos, limites de entrada e
falhas internas genéricas permanecem divergências deliberadas. Nenhum tráfego
moderno foi habilitado.

Persistem usuário Oracle com privilégios de proprietário, ausência de autenticação
/autorização, paginação sensível a concorrência, custo de offset, ausência de
certificação Oracle 19c/ERP externo/produção/carga. O controle de leitura é o SQL
fixo da aplicação, não uma restrição de privilégios do banco. Os testes não
certificam imutabilidade de cada linha diante do agendador legado preexistente.
Não houve escritas fiscais por esta execução. Encerrado após o relatório.

## Preparação do commit local

A limpeza para versionamento alterou somente documentação e evidências JSON.
Foram omitidos IPs privados, caminho pessoal do checkout, IDs de container/imagem,
horários de início e nomes de recursos Docker de outros projetos. O inventário
histórico de preservação foi resumido em contagens e escopo; não representa uma
nova inspeção nem permite reconstruir os identificadores omitidos. Nos resultados
de testes foram retirados caminhos absolutos, argumentos repetidos nos nomes e
ID local de transação; resultados, contadores e hashes originais foram mantidos.
Cópias da senha didática nos exemplos documentais deram lugar à configuração
existente do Compose. CPF/CNPJ sintéticos dos testes e corpus foram preservados:
são fixtures dos exemplos didáticos preexistentes, necessárias à reprodução.

Todos os 24 hashes de fontes da revisão foram conferidos antes da limpeza.
O hash de `novo/README.md` nesse JSON continua sendo o histórico da revisão;
após a limpeza documental ele difere intencionalmente. Código executável, testes,
corpus e baseline não foram alterados. Os relatórios Docker e suplementares
anteriores foram mantidos como evidência histórica distinta da revisão final.
Artefatos binários, obj, TRX e TestResults continuam fora do versionamento.

Validação desta preparação: JSONs, projetos XML, WSDL, OpenAPI e links locais
válidos; 46 requisições dos três corpora reproduzidas em memória com hashes e
expectativas conferidos; os 40 resumos do corpus original continuam iguais ao
baseline. Os 23 hashes não documentais da revisão permanecem idênticos.
`git diff --check` e a conferência de preservação de legado, db, fachada,
tools/exemplos e Dockerfile retornaram exit 0. Como a limpeza não alterou
conteúdo executável, não foi necessária nova execução das suítes nem acesso
Oracle/Docker. Os resultados de integração acima pertencem à revisão anterior.
