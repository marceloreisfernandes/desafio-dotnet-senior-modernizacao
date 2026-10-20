# Revisão do PR #212 — API de consulta e cancelamento de NFS-e

Marcelo, começar com ADO.NET é uma escolha compatível com esta extração: não precisamos introduzir um ORM para corrigir os problemas abaixo. Os parâmetros tipados com `BindByName` na consulta por número, o uso de `decimal` para valores fiscais e o descarte de comandos e leitores com `using` são bons pontos. Precisamos estender esses cuidados ao SQL opcional, à conexão e ao fluxo de cancelamento antes do merge.

**Decisão: solicitar alterações (Request changes).** São **13 achados: P0 = 0, P1 = 7, P2 = 6, P3 = 0**. R1–R11 bloqueiam o merge; R12–R13 são melhorias não bloqueantes neste incremento.

## Escopo e evidência

Revisão estática de [PR-212.md](PR-212.md) e [PR-212.diff](PR-212.diff), confrontada com o [desafio](../README.md#8-mentoria), os serviços legados, os repositórios Oracle, os packages, a trigger e o roteamento SOAP atual. Checkout conferido: `feat/fachada-soap-legado`, HEAD `a1b702a5ab54a4882ce999e68667dc48b4781701`, árvore inicialmente limpa.

Os caminhos `src/Nfse.Consulta.Api/...` e `tests/Nfse.Consulta.Tests/...` existem **no patch do exercício**, não como arquivos deste checkout. As linhas abaixo correspondem ao lado adicionado do patch; os links apontam para a linha correspondente em `PR-212.diff`. Não confundir esse código com a implementação existente em `novo/`.

Não executei os projetos do patch, chamadas à Receita, requisições SOAP nem operações Oracle. A execução em homologação relatada pelo autor não foi reproduzida. Todos os testes indicados abaixo são verificações propostas, não resultados obtidos.

## Defeitos que bloqueiam o merge

### R1 · P1 — Parametrizar também o filtro de situação

**Local:** `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:61–62` ([diff](PR-212.diff#L207)).

`situacao` vem da query string e é interpolada no SQL. Por exemplo, `N' OR '1'='1` produz uma condição verdadeira fora dos filtros de documento e período, permitindo consultar notas de outros tomadores. Os demais parâmetros não protegem esse fragmento.

**Correção:** usar `SITUACAO = :situacao` com `OracleDbType.Varchar2` e validar os estados aceitos (`N`, `C`, `S`). A validação complementa a parametrização.

**Verificação:** rejeitar o valor malicioso com 400; conferir o comando parametrizado e, em Oracle de teste isolado, garantir que a consulta de um tomador não retorne notas de outro.

### R2 · P1 — Exigir identidade e autorização sobre a nota ou o tomador

**Local:** `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:10–12,22,47–49` ([diff](PR-212.diff#L59)); `src/Nfse.Consulta.Api/Program.cs:13–18` ([diff](PR-212.diff#L388)).

Os três endpoints não exigem autenticação nem verificam a relação entre o chamador e os identificadores da URL. É possível solicitar dados fiscais e acionar cancelamento escolhendo outro prestador. `PrestadorRegularAsync` verifica cadastro, não a identidade de quem pediu. O PR não apresenta uma política do Istio que garanta essa autorização; CORS também não a substitui.

**Correção:** exigir autenticação no grupo e autorização por recurso, vinculando a identidade confiável ao prestador/tomador e à permissão de cancelar. Se a identidade vier do gateway, explicitar e verificar a fronteira de confiança e o bloqueio de acesso direto.

**Verificação:** requisição anônima recebe 401; usuário autenticado sem vínculo recebe 403 ou ocultação consistente com 404, sem consultar dados protegidos nem escrever; titular autorizado consegue operar.

### R3 · P1 — Preservar as regras fiscais ao cancelar

**Local:** `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:111–123` ([diff](PR-212.diff#L257)); `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:57–72` ([diff](PR-212.diff#L106)).

O `UPDATE` direto ignora as recusas por nota substituída e guia paga, presentes em [PKG_NFSE.CANCELAR_NFSE, linhas 89–119](../db/03_pkg_nfse.sql#L89). Também sobrescreve data/motivo em chamadas concorrentes ou repetidas, enquanto o package retorna sucesso sem nova alteração quando a nota já está cancelada. O endpoint rejeita essa repetição e a resposta monta uma data local, sem reler a data efetivamente gravada. O total de linhas afetadas é ignorado.

O `UPDATE` **dispara** a [trigger de recálculo, linhas 5–12](../db/04_triggers.sql#L5). Ela usa transação autônoma; portanto, apenas adicionar `BeginTransaction` no C# não torna nota e guia atômicas. Não cabe afirmar que o recálculo foi removido ou que sua consistência foi comprovada. O [legado, linhas 59–79](../legado/src/NotaCarioca.Dados/NfseRepositorio.cs#L59) chama o package em transação explícita. A consulta externa à Receita também acrescenta uma condição que o [serviço legado, linhas 38–45](../legado/src/NotaCarioca.Servicos/CancelamentoNfseServico.cs#L38) não impõe.

**Correção:** encapsular a chamada ao package existente, tratar seus códigos de negócio e explicitar commit/rollback, sem duplicar o recálculo no C#. Retornar o estado persistido. Caracterizar repetição, prazo e concorrência; justificar separadamente a nova exigência da Receita e eventuais diferenças REST. Não alterar o legado ou a trigger como efeito colateral deste PR.

**Verificação:** em Oracle isolado, cobrir nota inexistente, substituída, guia paga, repetição, fronteira do prazo pelo relógio do banco e duas solicitações concorrentes. Conferir nota, data/motivo e guia após sucesso e falha/rollback, inclusive o efeito autônomo herdado. Não presumir atomicidade nem repetir automaticamente uma escrita após timeout de resultado incerto.

### R4 · P1 — Corrigir o lifetime e a propriedade da conexão Oracle

**Local:** `src/Nfse.Consulta.Api/Program.cs:6,10–11` ([diff](PR-212.diff#L381)); `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:10–24` ([diff](PR-212.diff#L156)).

O singleton captura `ReceitaClient` scoped e mantém uma única `OracleConnection` para todas as requisições. A checagem de `State` não sincroniza aberturas ou comandos concorrentes. A conexão criada manualmente nunca é descartada pelo repositório, que não implementa descarte. Desabilitar `ValidateScopes` oculta a incompatibilidade de lifetimes.

**Correção:** registrar o repositório como scoped, restabelecer a validação e abrir/descartar a conexão por operação ou unidade transacional. Na transação, manter conexão e comandos no mesmo escopo. O pool permite esse uso sem exigir uma conexão global. Ver [orientação de DI](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines) e [garantias de concorrência de OracleConnection](https://docs.oracle.com/en/database/oracle/oracle-database/19/odpnt/OracleConnectionClass.html).

**Verificação:** resolver os serviços com validação de escopos habilitada; exercitar requisições concorrentes e falha de conexão em ambiente isolado, confirmando descarte e ausência de compartilhamento de sessão entre requisições.

### R5 · P1 — Remover a atualização de cache com `async void`

**Local:** `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:74–89` ([diff](PR-212.diff#L123)).

`AtualizarCacheSituacao` não pode ser aguardado. Exceções dessa rotina não chegam ao `catch` do endpoint e podem encerrar o processo depois do cancelamento. Além disso, ela consulta o **tomador**, embora a validação anterior e o cache relevante sejam do **prestador**; pode até enviar CPF à rota `cnpj/`. A operação continua usando o serviço da requisição após a resposta.

**Correção:** retirar essa atualização até haver necessidade demonstrada. Se for mantida, retornar `Task`, usar o documento correto e aguardar com tratamento explícito de falha. Uma falha acessória após a escrita não deve transformar um cancelamento confirmado em indicação para repeti-lo. Ver [retornos assíncronos em C#](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-return-types).

**Verificação:** simular erro HTTP e JSON inválido na atualização; confirmar que o processo permanece ativo e que o resultado fiscal confirmado continua inequívoco. Conferir qual documento foi enviado.

### R6 · P1 — Remover a credencial de homologação da configuração versionada

**Local:** `src/Nfse.Consulta.Api/appsettings.json:3` ([diff](PR-212.diff#L406)).

A configuração contém usuário, senha literal e destino de homologação. O [relato de teste](PR-212.md#como-testei) informa acesso a esse ambiente. Não validei a credencial nem a reproduzo aqui; se válida, o conteúdo versionado permite seu uso por quem tiver acesso ao repositório.

**Correção:** fornecer a conexão por configuração externa/segredo do ambiente; manter no arquivo apenas exemplo sem credenciais operacionais. Se o valor for real, revogar/rotacionar antes de republicar: removê-lo do arquivo não revoga a exposição anterior.

**Verificação:** confirmar que o serviço recebe a configuração externamente e que o diff não contém segredo operacional; registrar o tratamento da credencial exposta, quando aplicável, sem divulgar o valor.

### R7 · P1 — Conter exposição de erros e documentos fiscais

**Local:** `src/Nfse.Consulta.Api/Program.cs:15` ([diff](PR-212.diff#L390)); `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:32,74,79–82` ([diff](PR-212.diff#L128)); `src/Nfse.Consulta.Api/Infra/ReceitaClient.cs:23` ([diff](PR-212.diff#L328)).

`UseDeveloperExceptionPage` é ativado em qualquer ambiente e o cancelamento devolve `ex.Message` e `ex.StackTrace`. Falhas expõem detalhes internos ao cliente. Os logs também registram CPF/CNPJ integral, inclusive do tomador, sem necessidade demonstrada para diagnóstico.

**Correção:** restringir a página de desenvolvimento a Development, responder erros inesperados com ProblemDetails genérico e identificador de correlação, e manter detalhes técnicos em logs protegidos. Usar mensagens estruturadas e omitir/mascarar documentos pessoais.

**Verificação:** simular falhas Oracle/HTTP com ambiente Production e conferir corpo, cabeçalhos e logs: sem stack trace, configuração ou documento integral; erro rastreável pelo identificador.

### R8 · P2 — Distinguir ausência de nota de falha de infraestrutura

**Local:** `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:39–48` ([diff](PR-212.diff#L185)); `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:17–19,53–55` ([diff](PR-212.diff#L66)).

O repositório converte qualquer exceção em `null`. Assim, indisponibilidade do Oracle ou falha de mapeamento vira 200 com valor nulo na consulta, ou 404 no cancelamento. Isso informa ausência de dado quando houve falha e oculta a indisponibilidade dos consumidores.

**Correção:** reservar ausência para a leitura sem linhas, retornar 404 na consulta por número e propagar/traduzir falhas técnicas para o tratamento centralizado, com 5xx apropriado e mensagem segura.

**Verificação:** separar três cenários: nota existente → 200 com dados; inexistente → 404; Oracle indisponível → 5xx sem dados internos. Um cenário não pode satisfazer o teste do outro.

### R9 · P2 — Validar entradas e explicitar o intervalo da consulta REST

**Local:** `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:22–37,68` ([diff](PR-212.diff#L71)); `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:58–73,119` ([diff](PR-212.diff#L204)).

Não há limites para período, página ou tamanho: a requisição pode materializar um volume arbitrário de notas; `(pagina - 1) * tamanho` pode transbordar. Documento inválido chega a `long.Parse`, gerando exceção em vez de erro de entrada. Motivo maior que a coluna de 255 caracteres também chega ao Oracle ([schema, linha 84](../db/01_schema.sql#L84)). Para `fim=2026-09-30`, `BETWEEN` encerra à meia-noite e exclui o restante desse dia, contrariando a inclusão do dia final descrita no manual.

**Correção:** validar documento, identificadores positivos, ordem/limite de datas, página ≥ 1, tamanho máximo e offset seguro antes do banco; definir limite do motivo. Tratar CPF/CNPJ como texto com bind `Varchar2`, coerente com [TOMADOR_DOCUMENTO, linha 80](../db/01_schema.sql#L80), preservando zeros. Para datas civis inclusivas no REST, usar limite superior exclusivo do dia seguinte. Documentar tamanho padrão e semântica temporal: não impor silenciosamente essas escolhas ao SOAP, cujo [filtro legado, linhas 144–164](../legado/src/NotaCarioca.Dados/NfseRepositorio.cs#L144) tem peculiaridades próprias.

**Verificação:** documento inválido/com zero inicial; página zero; tamanho excessivo; offset extremo; período invertido/acima do limite; motivo excedente. Entradas inválidas recebem 400 antes de acessar dependências. Cobrir notas à meia-noite, ao fim do último dia e no início do dia seguinte.

### R10 · P2 — Tornar o cache seguro para concorrência e validade cadastral

**Local:** `src/Nfse.Consulta.Api/Infra/ReceitaClient.cs:7,20,34` ([diff](PR-212.diff#L312)); `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:107–108` ([diff](PR-212.diff#L253)).

O `Dictionary` estático é lido e escrito simultaneamente por requisições diferentes, sem sincronização. Os valores nunca expiram e o cache não tem limite: uma situação `ATIVA` ou irregular pode continuar decidindo cancelamentos mesmo após mudança cadastral. Trocar o lifetime de `ReceitaClient` não resolve o campo estático.

**Correção:** se a regra da Receita for mantida, usar cache com acesso concorrente seguro, expiração e limite definidos, com comportamento explícito quando a consulta falha. Um `ConcurrentDictionary` sozinho não resolve a validade nem o crescimento.

**Verificação:** chamadas paralelas para documentos iguais e diferentes; mudança de situação após expiração; falha da Receita. Garantir que uma falha não seja armazenada como regularidade e que a decisão use a validade acordada.

### R11 · P2 — Fazer os testes detectarem regressões reais

**Local:** `tests/Nfse.Consulta.Tests/NfseEndpointsTests.cs:16–34` ([diff](PR-212.diff#L467)).

`Assert.NotNull(resp)` passa para qualquer resposta HTTP, inclusive erro. O segundo teste aceita explicitamente 500 apesar do nome. A factory não substitui Oracle/Receita nem configura ambiente isolado; portanto, os testes dependem da configuração de homologação do PR. Nenhum cobre cancelamento ou autorização. O `Thread.Sleep` após receber a resposta não comprova comportamento adicional.

**Correção:** testar status e conteúdo exatos com dependências controladas para contratos REST e falhas; reservar Oracle real para integração isolada e configurada explicitamente. Incluir as verificações dos bloqueios acima, especialmente estado fiscal e recusas, sem exigir homologação compartilhada para executar a suíte. Remover a espera arbitrária.

**Verificação:** os testes precisam falhar se uma resposta esperada como 200 virar 500, se houver acesso cruzado ou se cancelamento alterar nota/guia indevidamente. Comprovar que a suíte comum não acessa homologação e registrar separadamente os resultados de integração.

## Melhorias não bloqueantes neste incremento

### R12 · P2 — Manter o caminho de I/O assíncrono até o endpoint

**Local:** `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs:37` ([diff](PR-212.diff#L86)); `src/Nfse.Consulta.Api/Infra/ReceitaClient.cs:38–40` ([diff](PR-212.diff#L343)); `src/Nfse.Consulta.Api/Infra/NfseRepository.cs:100–108` ([diff](PR-212.diff#L246)).

`.Result` bloqueia uma thread durante o I/O; a consulta à Receita ainda mantém o leitor e a conexão Oracle abertos enquanto espera a rede. Sob concorrência, isso pressiona threads e conexões. Não há evidência aqui de deadlock ou medição de carga.

**Correção:** usar `async`/`await` em toda a cadeia, liberar leitor/conexão após obter o cadastro e antes da chamada HTTP, e propagar cancelamento onde suportado. `ConfigureAwait(false)` não torna `.Result` não bloqueante.

**Verificação:** simular Receita lenta e requisição abortada, conferindo liberação de recursos e conclusão/cancelamento das tarefas. Comparar concorrência com latência controlada antes de ampliar tráfego.

### R13 · P2 — Reutilizar o transporte HTTP e descartar a resposta

**Local:** `src/Nfse.Consulta.Api/Infra/ReceitaClient.cs:25–32` ([diff](PR-212.diff#L330)).

Cada consulta fora do cache cria e descarta um `HttpClient` com seu próprio pool, perdendo reutilização de conexões. `HttpResponseMessage` não tem descarte explícito. Isso cria custo e risco de exaustão de portas sob volume; não foi constatada exaustão neste review.

**Correção:** usar `IHttpClientFactory` com cliente configurado, descartar a resposta com `using` e definir timeout/cancelamento compatíveis com o orçamento da requisição. Tratar resposta vazia/malformada como falha da dependência. Ver [orientação oficial de HttpClient](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines).

**Verificação:** com handler controlado, cobrir timeout, status não exitoso, corpo vazio e JSON inválido; verificar descarte inclusive nas falhas e reutilização do handler configurado.

## Compatibilidade SOAP e condições mínimas para aprovação

Este patch adiciona REST; não contém adaptador SOAP nem alteração de roteamento. Isso não demonstra paridade, mas a ausência dessas entregas não é um bloqueio adicional para um incremento explicitamente REST. O [roteamento atual, linhas 19–25 e 53–60](../fachada/src/NotaCarioca.Fachada/SoapRouting.cs#L19) encaminha o contrato `/NfseWS.asmx`; não se deve apontá-lo para esses endpoints JSON. A semântica SOAP e suas particularidades precisam de caracterização antes de uma futura troca de destino. Em especial, cancelamento não deve ser executado em dois destinos para comparação em sombra.

Para aprovar este PR, preciso de **R1–R11 corrigidos e das respectivas verificações com resultados registrados**, incluindo integração fiscal em Oracle isolado e evidência da fronteira de autorização. Podemos manter o escopo pequeno com ADO.NET e o package existente; não é necessário introduzir uma nova arquitetura ou concluir todas as entregas do desafio neste PR. R12–R13 podem seguir em acompanhamento antes da ampliação de tráfego.

**Veredito final: solicitar alterações.** Os riscos de acesso indevido e alteração fiscal fora das regras impedem o merge nesta versão. Os cenários e testes propostos tornam a próxima rodada de revisão objetiva.
