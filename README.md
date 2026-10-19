# Desafio Técnico — Engenheiro(a) de Software Sênior — Modernização (.NET)

## Contexto

O **Nota Carioca** é a plataforma de NFS-e do Rio de Janeiro: um monolito .NET Framework + Oracle 19c que processa milhões de notas fiscais por ano. Milhares de ERPs de contribuintes conversam com ele por web services SOAP, e muitos não são atualizados há anos. Um novo app e o novo portal do contribuinte precisam de uma API moderna.

A missão do time de modernização é tirar capacidades de dentro do monolito **sem que nenhum contribuinte perceba**: sem big bang, sem janela de manutenção, com caminho de volta a qualquer momento.

Este repositório contém uma **réplica didática** do monolito, com C# no estilo .NET Framework, VB.NET, regras dentro de packages e triggers PL/SQL e um endpoint SOAP (`/NfseWS.asmx`). Sua tarefa é extrair dele a **consulta e o cancelamento de NFS-e**.

## O legado

O primeiro incremento implementado é uma fachada SOAP que encaminha todas as
operações ao legado. Instruções de execução, verificações somente de leitura e
rollback estão em [fachada/README.md](fachada/README.md). A extração de serviços
descrita no restante do desafio ainda não faz parte deste incremento.

```
legado/
  src/NotaCarioca.Dominio/      VB.NET — cálculo de ISS, validação de CPF/CNPJ, competência
  src/NotaCarioca.Dados/        C# — acesso a dados (ADO.NET / ODP.NET)
  src/NotaCarioca.Servicos/     C# — lotes de RPS, consultas, cancelamento, geração de XML
  src/NotaCarioca.WebServices/  host que emula o endpoint ASMX (/NfseWS.asmx)
db/                             schema, packages (PKG_NFSE, PKG_GUIA), triggers e cargas
tools/                          gerador de lotes de RPS e exemplos de requisições SOAP
```

```bash
docker compose up -d --build     # Oracle + legado
# a primeira subida cria o schema e carrega ~2 milhões de notas: leva alguns minutos
curl http://localhost:8080/health
just lote 1000                   # emite notas novas enviando um lote com 1.000 RPS
```

O legado roda em `net48` e `net8.0`. Em produção ele roda em .NET Framework 4.8 sobre IIS, e o host em `NotaCarioca.WebServices` só existe para rodar a réplica em qualquer sistema operacional. O banco local é Oracle 23ai Free, mas produção é 19c.

### Operações SOAP

| Operação | O que faz | Escopo do desafio |
| --- | --- | --- |
| `RecepcionarLoteRps` | recebe um lote de RPS; o processamento é assíncrono | fica no legado |
| `ConsultarSituacaoLoteRps` / `ConsultarLoteRps` | situação e resultado do lote | fica no legado |
| `ConsultarNfse` | prestador consulta as notas que emitiu | **extrair** |
| `ConsultarNfseServicoTomado` | tomador consulta as notas que recebeu | **extrair** |
| `CancelarNfse` | prestador cancela uma nota | **extrair** |

### Manual de integração (resumo)

É o que os contribuintes receberam há anos:

- Datas e horas seguem a ISO 8601.
- CNPJ tem 14 caracteres e CPF tem 11.
- O período de consulta vai de `DataInicial` a `DataFinal`, inclusive, com no máximo 31 dias.
- As consultas são paginadas, com 50 notas por página.
- Uma nota pode ser cancelada até o dia 10 (inclusive) do mês seguinte à competência, exceto se a guia da competência já estiver paga ou se a nota tiver sido substituída.
- Ao cancelar uma nota, a guia da competência é recalculada.

Não há outra documentação. Como em todo legado, **o comportamento em produção é a especificação real**, e ele nem sempre bate com o manual.

## O que construir

### 1. A fachada (strangler fig)

Todo o tráfego de `/NfseWS.asmx` passa a entrar por uma fachada sua. Ela decide, **por operação**, quem atende:

- **`legado`**: comportamento de hoje.
- **`sombra`**: o cliente recebe a resposta do legado; o serviço novo processa a mesma requisição e as respostas são comparadas. Cada divergência é registrada com o suficiente para ser reproduzida, sem expor dados pessoais. A sombra não pode piorar a latência nem a disponibilidade para o cliente.
- **`novo`**: o serviço novo atende.

O roteamento precisa ser controlável por prestador (lista de inscrições) e por percentual de tráfego, alterável **em tempo de execução, sem deploy**, com um kill switch que devolva tudo ao legado em segundos. As operações fora do escopo continuam indo para o legado sem nenhuma alteração.

Pense no que "sombra" significa para cada operação. Elas não são todas iguais.

### 2. O serviço novo

Em **.NET 8+**, com arquitetura limpa e CQRS onde fizer sentido. Ele expõe:

- **uma API REST** para o app e o portal. O design é seu: versionamento, paginação, erros (ProblemDetails), OpenAPI;
- **uma camada anticorrupção** que fala o contrato SOAP do legado, para que a fachada possa direcionar os ERPs ao serviço novo sem que eles mudem nada.

O domínio do serviço novo não conhece XML, SOAP nem os códigos e convenções do legado.

### 3. Dados e regras de negócio

Durante a transição, o legado continua emitindo notas e mantendo as guias. As regras de cancelamento e o efeito do cancelamento na guia estão hoje no banco (`PKG_NFSE`, `PKG_GUIA`, triggers). Cabe a você decidir de quem são os dados nessa fase, se a regra continua no banco, é encapsulada ou é reimplementada, e de onde o lado de consulta lê. Não existe resposta certa. Esperamos uma decisão bem justificada, com plano de reversão.

### 4. Paridade comprovada

Os ERPs dependem de comportamentos que **não estão no manual**. Encontre-os e decida, caso a caso, o que preservar no contrato SOAP e o que corrigir na API nova. Esperamos:

- testes de caracterização do legado (golden master) que rodem contra o legado e contra o serviço novo;
- testes de contrato para a API REST;
- divergências da sombra explicadas: cada uma é bug do novo, bug do legado ou decisão consciente.

### 5. Observabilidade

- Traces OpenTelemetry de ponta a ponta (fachada → legado ou novo → Oracle) com propagação W3C.
- Logs estruturados correlacionados ao trace.
- Métricas de requisições e latência por operação e modo, e de divergências na sombra.
- Um backend de traces e métricas no compose (Jaeger, Tempo, SigNoz, o que preferir).

Instrumentar o legado é opcional. A fachada precisa dar visibilidade sobre ele.

### 6. Entrega e operação

- `docker compose up` sobe tudo do zero: Oracle, legado, fachada, serviço novo e observabilidade.
- Manifests Kubernetes (Kustomize ou Helm) para a fachada e o serviço novo, com probes, recursos, configuração das flags e estratégia de rollout.
- CI (GitHub Actions) com build, testes unitários, de integração (Oracle real), de contrato e de paridade, além do build de imagens.

### 7. Decisões e plano

- **ADRs** em `docs/adr/`. No mínimo: estratégia e ordem de extração; propriedade dos dados na transição; o que fazer com as regras em PL/SQL; o tratamento dos comportamentos legados encontrados.
- **`docs/plano-de-migracao.md`**: critérios objetivos para avançar cada operação de `legado` → `sombra` → `novo` → desligamento no monolito, o que monitorar em cada fase e como voltar atrás.

### 8. Mentoria

Um colega do time de legado, muito experiente em .NET Framework e Oracle e começando agora em .NET 8, abriu o primeiro PR dele no serviço novo: `revisao/PR-212.md` e `revisao/PR-212.diff`. Escreva sua revisão em `revisao/REVIEW-PR-212.md`, como comentaria no GitHub. Ele vai ler cada comentário, e o objetivo é que ele aprenda, não só que o PR seja corrigido.

## Restrições

- **O legado está em produção.** Mudanças no código ou no banco do legado devem ser mínimas, justificadas em ADR e compatíveis com .NET Framework 4.8 / C# 7.3 e Oracle 19c.
- Objetos de banco novos ficam em schema próprio. Não altere as tabelas do legado.
- Nenhuma operação de escrita pode ser executada duas vezes por causa da sua arquitetura.

## O que entregar

Um **fork público** deste repositório, com histórico de commits que mostre a evolução do trabalho e um README explicando: como rodar, como alternar os modos de cada operação e ver o resultado, as decisões principais (com links para os ADRs) e o que você faria com mais tempo.

## O que olhamos

| Critério | Peso |
| --- | --- |
| Strangler fig de verdade: roteamento por operação, sombra segura, rollback instantâneo | Alto |
| Camada anticorrupção: o legado não vaza para o domínio novo, e os ERPs não percebem a troca | Alto |
| Comportamentos não documentados: encontrados, testados e tratados com decisão explícita | Alto |
| Decisões de dados e regras em PL/SQL: justificativa, riscos e reversibilidade | Alto |
| Código .NET 8: arquitetura, async, testes, clareza | Alto |
| ADRs e plano de migração: um time conseguiria executar a partir deles? | Alto |
| Observabilidade útil para operar a migração, não só "tem OpenTelemetry" | Médio |
| Docker, Kubernetes e CI | Médio |
| Revisão do PR-212: prioridade, didática e tom | Médio |

Profundidade vale mais do que cobertura. Uma operação extraída com sombra, paridade e rollback bem resolvidos vale mais do que três operações "migradas" sem garantias.

## Diferenciais

- Lado de consulta com modelo de leitura próprio, alimentado por CDC ou outbox, com a defasagem medida
- Teste de resiliência: o que acontece com o cliente quando o serviço novo, o legado ou o banco ficam lentos ou caem
- Teste de carga (k6) comparando legado e novo, com thresholds justificados
- Deploy em cluster local (kind/k3d) com rollout progressivo
- Flags com OpenFeature ou similar

## FAQ

**Preciso de Windows?**
Não. O legado compila em `net48` com reference assemblies e roda em `net8.0` em qualquer sistema.

**Preciso extrair as três operações?**
Esperamos as três. Se precisar priorizar, faça uma bem completa, explique por que escolheu essa e como faria as outras.

**Posso usar outro banco no serviço novo?**
Pode, desde que a decisão esteja em ADR e o dado continue consistente com o que o legado grava.

**Quanto de máquina preciso?**
O container Oracle usa cerca de 2–3 GB de RAM. Planeje o resto do compose levando isso em conta.

**Como será a entrevista?**
Vamos discutir suas decisões e pedir que você altere partes da entrega ao vivo.

---

Dúvidas: **selecao.pcrj@gmail.com**
