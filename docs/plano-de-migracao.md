# Plano de migração incremental — Nota Carioca

## Escopo e estado de referência

Plano elaborado em 20/10/2026 sobre a branch `feat/fachada-soap-legado`, HEAD
`44c1315dcbaea02b936ffc03d240a34096706cb5`, para atender ao
[desafio original](../README.md). A ordem é `ConsultarNfse`,
`ConsultarNfseServicoTomado` e, após decisões adicionais de escrita, `CancelarNfse`.
Cada operação tem seu próprio aceite: promover a primeira não promove as demais.
Recepção, emissão e consulta de lotes permanecem no legado.

Este documento organiza a execução e seus gates; não habilita tráfego nem atesta
prontidão de produção. Não propõe outra arquitetura: mantém fachada, serviço de
consulta e Oracle compartilhado conforme as [ADRs 0001](adr/0001-fachada-soap-legado.md)
e [0002](adr/0002-consultar-nfse-independente.md). A propriedade dos dados continua
no Oracle legado, sem replicação, CDC, migração de tabelas ou recálculo de ISS na
consulta nova. Regras e efeitos de cancelamento continuam no PL/SQL existente.
Qualquer objeto futuro deve respeitar o schema próprio exigido pelo desafio;
este plano não autoriza alterar tabelas, packages ou triggers.

### Implementado e verificado localmente

As evidências abaixo são **históricas, versionadas**, de 19/10/2026. Nesta elaboração
houve leitura estática; não foram repetidos testes, consultas Oracle, sondas HTTP
ou inspeções Docker. Contagens de rodadas diferentes não devem ser somadas nem
tratadas como um único ensaio sobre todos os binários atualmente implantados.

| Capacidade | Evidência e alcance |
| --- | --- |
| Fachada, WSDL integral, encaminhamento opaco, falhas e cancelamento de transporte | [Verificação inicial](verificacao-fachada.md), testes `ProxyTests` e incremento descrito no [README da fachada](../fachada/README.md). O relatório inicial registra o timeout antigo; a configuração atual é 660 s. |
| Roteamento runtime de `ConsultarNfse`, allowlist, percentual, sombra e kill switch | `RoutingTests` e validação Compose no README da fachada: modos reais, troca atômica e preservação de resposta; testes controlados de saturação, falhas e ausência de replay. Tempos locais de troca não são SLA. |
| `ConsultarNfse` independente, REST v1 e adaptador SOAP | [Verificação](verificacao-consultar-nfse.md) e [revisão](revisao-consultar-nfse.md): revisão registra 93/93 testes, incluindo 7 Oracle; corpus original com 40/40 respostas completas idênticas, mais casos suplementares e de revisão com divergências deliberadas. |
| Proteção de encaminhamento das escritas | Validação da fachada com upstreams falsos: uma chamada legada e zero modernas por escrita sintética, sem retry. Não comprova cancelamento fiscal real ou atomicidade Oracle. |

### Implementado, com validação incompleta

`ConsultarNfse` lê Oracle por SELECTs parametrizados, preserva valores persistidos
e materializa até 50 notas, usando uma leitura adicional para indicar a próxima
página. Há consistência por instrução, sem snapshot comum entre comandos, páginas
ou chamadas de legado e novo. A revisão demonstrou que o antigo `SET TRANSACTION
READ ONLY` isolado era ineficaz sob autocommit; ele foi removido. SQL somente de
leitura não restringe a credencial: o Compose ainda usa usuário proprietário.

O SOAP preserva comportamentos reais como fim exclusivo, E040/200 na página vazia,
ordem de validação, peculiaridades de paginação e formatação fiscal. A REST tem
validação própria, fim inclusivo e ProblemDetails; sua semântica não é o oráculo
de compatibilidade SOAP. A matriz detalhada está na ADR 0002.

Persistem diferenças deliberadas de XML estrito, limites de entrada e mensagens
internas genéricas. Não há paridade total demonstrada para documentos Unicode,
dados nulos/corrompidos, offsets/DST históricos, concorrência, todos os erros Oracle
ou ERPs externos. Páginas de 49/50/51 linhas foram verificadas com leitor controlado,
sem criar esse volume fiscal real. Oracle local é 23ai Free; produção 19c e IIS/
.NET Framework 4.8 ainda precisam de certificação no ambiente correspondente.

### Planejado ou ausente

Não há extração de `ConsultarNfseServicoTomado` nem de `CancelarNfse`, flags para
essas operações, autenticação/autorização nova, métricas persistentes, auditoria
central de flags ou observabilidade OpenTelemetry de ponta a ponta com backend.
Não há manifests Kubernetes nem evidência de carga representativa ou produção.
O CI atual compila o legado net48/net8.0 e executa testes net8.0 do legado,
fachada e consulta sem categoria `Oracle`; não provisiona Oracle, não executa
integração real e não constrói imagens. Sua execução hospedada não foi verificada
para este plano. Esses requisitos de entrega continuam pendentes.

## Controle de tráfego existente

O [arquivo versionado](../fachada/config/routing.json) começa com
`KillSwitch=true`, `Mode=legado`, `Percentage=0`, `Providers=[]`. Isso descreve o
checkout, não uma inspeção do runtime. Somente a chave `ConsultarNfse` é aceita.
Adicionar outra operação hoje torna o snapshot inválido e devolve o tráfego ao
legado; não habilita nova extração.

A seleção aplica kill switch, elegibilidade SOAP, allowlist de IM e coorte
percentual antes do modo. `Providers` preenchido exige IM exata após Trim,
preservando zeros; vazio permite qualquer IM elegível. A IM é declarada pelo
cliente e **não autentica nem autoriza** acesso. A lista e o percentual se
combinam; não são alternativas.

O bucket é os primeiros quatro bytes de SHA-256 de
`UTF8("ConsultarNfse:" + IM)`, interpretados como uint32 big-endian, módulo 100.
Seleciona-se quando `bucket < Percentage`. A coorte é estável por IM entre
processos/reinícios: percentual de buckets não garante igual percentual de
requisições ou prestadores reais. Uma allowlist pequena pode não selecionar
ninguém em 1%; para um piloto nominal, usar lista explícita com 100% e conferir
a seleção. O volume efetivo precisa ser medido antes de ampliar.

Somente POST SOAP 1.1 canônico de `ConsultarNfse`, com wrapper/inputXML/IM
inequívocos, UTF-8 inspecionável, Content-Length conhecido até 1 MiB, sem query,
compressão ou transferência chunked, pode sair do legado. Entradas não elegíveis,
ações desconhecidas e todas as escritas seguem opacas ao legado. DTD e profundidade
excessiva não são expandidos para selecionar rota.

O processo lê snapshot de até 16 KiB por decisão e relê após upload/inspeção.
Ausência, inacessibilidade ou configuração inválida falham para legado, sem
reutilizar estado permissivo. O diretório é montado somente para leitura no
container; o operador substitui o arquivo completo atomicamente no mesmo
diretório, conforme o [procedimento operacional](../fachada/README.md).
Não é necessário deploy para mudar flags. URLs, timeouts e capacidade são de
startup, com procedimento separado.

Em `sombra`, o cliente recebe a resposta integral legada; somente depois de sua
cópia é submetida a leitura moderna. Reserva/fila/consumidores são limitados
(duas vagas por processo por padrão), deadline da chamada sombra é 2 s e as
respostas têm captura de até 2 MiB em memória. Saturação descarta a observação;
resposta legada incompleta, comprimida ou excedente não é comparada. Não há
persistência de payload nem cópia de cookies/credenciais para a sombra.
Isso limita o trabalho da fachada, mas duplica SELECTs e não garante interrupção
imediata do trabalho Oracle após cancelamento.

Em `novo`, há uma tentativa ao moderno, sem fallback/replay para legado em falha.
O WSDL público integral e `/health`/`/health/ready` continuam no legado;
`/health/live` verifica só o processo da fachada. Readiness verde da fachada não
certifica o moderno. O WSDL parcial interno não substitui o contrato público.

O kill switch retorna decisões seguintes ao legado na próxima leitura. O objetivo
de poucos segundos precisa ser ensaiado no filesystem/mount alvo: não há garantia
de tempo real com volume travado nem atualização conjunta de réplicas.
Chamadas já despachadas e sombras admitidas não são desfeitas.

## Critérios comuns de promoção e interrupção

**Todos os números desta seção são propostas iniciais**, não baselines medidos,
SLOs contratados ou alertas implementados. Devem ser aprovados pelo responsável
da operação, equipe de sustentação e DBA após medir a linha de base. Até haver
coleta confiável, o gate permanece pendente; ausência de dados não é sucesso.

Essa classificação como proposta também se aplica a todos os limiares numéricos
de promoção, interrupção e retirada nas etapas e nos gates específicos abaixo,
incluindo durações, percentuais de exposição e reconciliação. Esses valores não
representam baselines medidos em produção.

A linha de base deve registrar, por operação e faixa de carga/paginação, volume,
p50/p95/p99 de ponta a ponta, erros técnicos, timeouts e SOAP Faults. Separar
rejeições de negócio esperadas de indisponibilidade: HTTP 200 com E040 não é erro
técnico. Medir CPU, memória, conexões/pool, filas, sessões Oracle, tempo de banco,
leituras lógicas/físicas e esperas/bloqueios com apoio do DBA. Comparações devem
usar janelas equivalentes de volume e mistura de requisições. Capacidade absoluta
e orçamento de latência precisam ser definidos antes do piloto; percentuais
isolados sobre uma linha de base já saturada não autorizam promoção.

| Dimensão | Gate proposto de promoção | Ação proposta de interrupção/rollback |
| --- | --- | --- |
| Contrato SOAP | 100% do corpus aprovado em status, Content-Type e bytes; WSDL público estruturalmente igual exceto endereço; testes de erros e clientes ERP representativos aprovados. Exceções deliberadas precisam de aceite explícito e escopo conhecido. | Qualquer incompatibilidade nova confirmada bloqueia promoção e retira a coorte afetada do novo; impacto fiscal/segurança aciona kill switch imediatamente. |
| Divergência inesperada | Zero divergências inesperadas não resolvidas; mínimo de 10.000 pares comparáveis e sete dias consecutivos incluindo pico. Operação de baixo volume exige janela maior ou corpus adicional com aceite explícito, sem reduzir silenciosamente o gate. | Uma divergência fiscal confirmada interrompe imediatamente. Taxa inesperada >0,1% em janela de 15 min com pelo menos 1.000 pares interrompe o ensaio para investigação, mesmo antes de confirmar causa. |
| Latência e erros | p95 e p99 até 10% acima da linha de base equivalente, dentro do orçamento absoluto aprovado; erros técnicos sem aumento >0,1 ponto percentual e taxa total <1%. Avaliar legado sob sombra e coortes em novo. | p95/p99 >20% acima da referência, ou aumento de erros >0,5 ponto percentual, em duas janelas consecutivas de 5 min com pelo menos 100 chamadas cada: reduzir exposição/acionar kill switch. Indisponibilidade confirmada não espera a janela. |
| Oracle e recursos | Aumento até 10% de CPU, tempo de banco e leituras por carga equivalente; pelo menos 30% de folga nos limites aprovados de CPU, memória, sessões e pools; sem novos bloqueios, timeouts de pool ou OOM. | CPU/memória/sessões acima de 85% da capacidade aprovada por 5 min, pool esgotado ou novo bloqueio com impacto: suspender sombra e voltar chamadas seguintes ao legado; reavaliar capacidade antes de retomar. |
| Operação | Coleta e alertas testados, responsáveis nomeados, runbook de incidente/reconciliação ensaiado, segredo e acesso restritos, probes de ambos os caminhos e artefato implantado identificados. Kill switch em todas as réplicas demonstrado em até 5 s para novas decisões. | Falta de controle de flags, evidência ou suporte impede promoção; rollback inoperante interrompe a expansão e exige contenção operacional. |
| Consistência fiscal | Nenhuma diferença não explicada em número, situação, competência, valores de ISS, retenção, cancelamento e substituição. Para escrita futura: 100% dos comandos do piloto reconciliados com nota e guia, sem pendências ambíguas. | Qualquer efeito duplicado ou inconsistência nota/guia: suspender novas escritas migradas e iniciar reconciliação com DBA e responsável fiscal; mudar rota não repara dados. |

A taxa de divergência é `unexpected-divergence / pares comparáveis concluídos`.
Registrar também `parity`, exceções estritas aprovadas, elegíveis, admitidas,
descartadas por capacidade/limite, falhas e timeouts. Observações perdidas não
entram como paridade. Proposta adicional: pelo menos 95% das observações admitidas
devem concluir comparação; perdas maiores bloqueiam promoção até explicar o viés.
Esse percentual não elimina a necessidade de cobrir prestadores, erros, páginas e
períodos representativos fora da amostra selecionada.

Hoje a sombra emite categorias fixas em logs, sem payload, identificadores ou
contexto suficiente para reproduzir cada requisição. Só `expected-strict-xml`
reconhece a exceção estreita documentada de caractere de controle com E001 exato;
outros desvios não devem ser reclassificados genericamente como esperados.
DTD/profundidade podem permanecer no legado sem gerar par comparável.
O requisito de divergências reproduzíveis com proteção de dados ainda está
incompleto. Antes da adoção, definir coleta agregada, correlação e reprodução
controlada/sanitizada, retenção e acesso, sem habilitar logging de corpos fiscais.
Investigar concorrência em ambiente controlado e dados estáveis; não normalizar
valores, ordem ou campos para fazer uma comparação passar.

## Etapas por operação

### 1. Linha de base legada

Manter kill switch ativo e medir pelo menos sete dias representativos, incluindo
pico; incluir fechamento/pagamento de competência na caracterização de escrita.
Registrar versão do código, imagens, configuração, timezone e schema efetivos.
Executar testes de caracterização/contrato e comparação de WSDL contra o host alvo,
sem usar scripts de emissão/cancelamento para fabricar paridade em base fiscal.
Os exemplos didáticos e a evidência local não substituem esse levantamento.

Saída: corpus e divergências conhecidos aprovados, orçamento absoluto de latência/
recursos definido, gates operacionais atendidos e retorno ao legado ensaiado.
Sem esses requisitos, continuar no legado. Para consultas, provisionar credencial
restrita de leitura antes de tráfego real; autenticação e autorização por prestador
ou tomador devem estar definidas, sem confiar na IM do envelope.

### 2. Validação em sombra — somente consultas

Para `ConsultarNfse`, começar com allowlist pequena e percentual 100 dentro dela,
ampliando gradualmente cobertura sob os gates comuns. O cliente continua no legado.
Medir impacto também sobre emissão, lotes e demais usuários do Oracle. Interromper
a sombra se a leitura duplicada consumir o orçamento compartilhado, mesmo que sua
resposta pública continue correta. O processamento assíncrono posterior à cópia
reduz acoplamento, mas não prova impacto nulo de CPU/memória/banco sob carga.

Saída: gates de contrato, amostragem, divergência, carga e operação atendidos por
sete dias consecutivos após a última correção relevante. Voltar a `legado` ou
ligar o kill switch se falhar; recomeçar a janela de evidência após a correção.

`ConsultarNfseServicoTomado` só entra nessa etapa depois de sua implementação,
caracterização e suporte explícito de roteamento. `CancelarNfse` não participa
de sombra executável: sua etapa equivalente é caracterização e simulação sem
efeito fiscal, descritas abaixo.

### 3. Adoção controlada pelo serviço novo

Promover `ConsultarNfse` para `novo` primeiro em allowlist nominal pequena já
validada; acompanhar ao menos 24 h e um pico por degrau. Depois de encerrar o piloto
nominal, ampliar para buckets de 1%, 5%, 25%, 50% e 100%, sempre verificando o volume
efetivo e os gates. Não retirar a allowlist e elevar o percentual sem considerar
a população adicional selecionada. Cada alteração exige responsável, horário,
versão do arquivo, resultado das sondas e decisão registrada fora de dados fiscais.
Não há auditoria automática disso hoje.

No modo `novo` não há comparação sombra automática com o legado. Sustentar o gate
de contrato com ensaios controlados de leitura, corpus e verificação independente
de consistência, sem presumir métricas de divergência inexistentes nesse modo.
Falha retorna ao operador; a requisição não é reenviada transparentemente.
O rollback usa o kill switch para chamadas seguintes e preserva evidência.

Saída de cada degrau: gates atendidos, nenhuma ocorrência fiscal pendente,
dependências legadas saudáveis e retorno demonstrado. A futura consulta por tomador
repete esse processo com seu próprio corpus. Escrita futura exige piloto exclusivo
por comando, sem duplicação, além dos gates fiscais adicionais.

### 4. Migração integral da operação

`Percentage=100` e `Providers=[]` significam 100% **das chamadas elegíveis**,
não 100% do contrato público. Mesmo assim, entradas fora do subconjunto aceito
continuam indo ao legado, assim como WSDL/health e outras operações.
Portanto essa flag sozinha não conclui a migração integral.

Proposta: manter 100% elegível por no mínimo 30 dias, incluindo fechamento de
competência, com todos os gates. Inventariar chamadas ainda legadas e tratar cada
classe: compatibilidade adicional ou mudança de contrato/cliente aprovada.
Mudanças de código necessárias são futuras e exigem revisão/testes próprios.
Saída: toda a população suportada atendida e certificada no novo, dependências
residuais identificadas e nenhuma exceção/ambiguidade sem decisão. Conservar
caminho legado utilizável durante a janela de reversão acordada.

### 5. Retirada eventual da operação legada

Exigir evidência confiável de zero despachos residuais para aquela operação por
pelo menos 30 dias após cobertura integral, aceite dos responsáveis por ERPs,
sustentação, segurança, DBA e área fiscal, além de plano de restauração ensaiado.
Essas contagens não existem como métricas prontas hoje. Documentar o fim da janela
de reversão e o tempo de recuperação aceito antes de remover o handler.

Não retirar o WSDL público integral nem desligar o monolito inteiro: outras ações
continuam nele. Hoje WSDL e readiness ainda dependem do host legado, e o kill
switch pressupõe a operação legada disponível. Remover o handler exige resolver
essas dependências em decisão posterior; após removê-lo, kill switch deixa de ser
rollback suficiente e restaurar código/configuração passa a ser necessário.
Remover cancelamento do host tampouco autoriza remover packages/triggers usados
pela emissão ou pelo processo fiscal. Arquivamento e retenção precisam de aceite
operacional/fiscal, sem exclusão de dados ou volumes como mecanismo de migração.

## Particularidades das operações

### ConsultarNfse

É a única extração implementada e selecionável hoje. A comparação original de 40
casos e os casos de revisão comprovam um recorte de comportamento, com as limitações
da ADR 0002. Antes da sombra real, completar segurança, telemetria, credencial
restrita, testes Oracle 19c/ERP e carga com offsets profundos. Não existe índice
composto `(PRESTADOR_ID, DT_EMISSAO, ID)`; nenhum ganho de latência deve ser assumido.
Decisões sobre divergências deliberadas exigem aceite antes de atender ERPs com
`novo`, mesmo quando a mudança é de segurança. As entradas preservadas no legado
devem constar do inventário que impede retirada prematura.

### ConsultarNfseServicoTomado

Não extraída. O caminho atual é `ConsultaNfseServico.ConsultarNfseServicoTomado`
→ `NfseRepositorio.ConsultarServicoTomado`. Caracterizar precedência
`Consulente`/`Tomador`, CPF/CNPJ, conversão numérica do documento, zeros iniciais,
período, E040 e paginação. O SQL contém `FULL(n)`, `TRUNC(DT_EMISSAO)` e parâmetro
Int64 para documento: são pontos para investigar paridade e custo, sem assumir
que reproduzir ou remover o hint resolve o problema. A passagem pela fachada foi
comparada localmente ao legado; isso não verifica um serviço novo para essa ação.

Antes de sombra, decidir identidade/autorização do tomador e seleção de coortes:
a allowlist de IM da primeira consulta não atende automaticamente esse contrato.
Implementar futuramente adaptadores, testes e roteamento específico revisado;
até lá não inserir essa chave no JSON. Depois seguir linha de base → sombra →
adoção → cobertura integral → retirada, com gates independentes.

### CancelarNfse

Não extraída, sem rota moderna ou endpoint REST novo. O fluxo atual valida no
serviço legado, abre transação em `NfseRepositorio.Cancelar`, chama
`PKG_NFSE.CANCELAR_NFSE`, faz commit e então relê a nota para construir a resposta.
O package trata nota ausente, já cancelada, substituída, prazo e guia paga.
O prazo usa `SYSDATE < ADD_MONTHS(competencia, 1) + 10`; caracterizar limites reais
de data/hora, sem substituir a expressão por interpretação do manual.

O UPDATE de situação aciona `TRG_NFSE_SITUACAO_GUIA`, com
`PRAGMA AUTONOMOUS_TRANSACTION`, que chama `PKG_GUIA.RECALCULAR_GUIA` e faz commit
independente. O recálculo soma ISS de notas normais não retidas usando a fórmula
do package e só atualiza guia aberta. Por inspeção, a transação autônoma não tem
a mesma visibilidade da alteração ainda não confirmada pela transação chamadora;
há risco de guia calculada com estado anterior e efeitos independentes de rollback.
Este é um risco estático a reproduzir em ambiente isolado, não uma falha fiscal
medida nesta elaboração. Reimplementar a regra em C# não resolve por si a
consistência transacional e pode criar duas fontes de regra.

Antes de qualquer adoção, registrar decisão adicional sobre manter/encapsular ou
reimplementar PL/SQL, propriedade da transação, concorrência com pagamento e
substituição, autorização fiscal, deduplicação e reconciliação. A decisão ainda
não foi tomada. O retorno sem erro para nota já cancelada não prova idempotência
completa, serialização concorrente ou segurança de repetir comandos.

**Nenhuma escrita deve executar em sombra.** Enviar o cancelamento a dois caminhos
pode duplicar efeitos, recalcular guias e produzir bloqueios mesmo quando uma
resposta é descartada. Executar e depois dar rollback também é inadequado diante
de efeitos autônomos. A validação equivalente deve usar corpus e doubles sem
efeito fiscal, avaliação somente de leitura e testes de escrita em base isolada
descartável, com autorização específica; não há simulador sem efeitos implementado
hoje. Só consultas comprovadamente sem efeitos podem ser duplicadas.

Gate adicional: provar no Oracle alvo que cada comando tem um único executor,
nota e guia permanecem consistentes e concorrência/falhas antes/depois do commit
não geram duplicação. Cobrir nota inexistente, já cancelada, substituída, prazo,
guia paga, pagamento concorrente, falha no recálculo e perda de resposta após
commit. Reconciliar 100% dos comandos do piloto, inclusive motivo/data/situação
da nota e valor/situação da guia. Sem essa decisão e evidência, continuar no legado.

## Falhas, timeout e limites de rollback

O timeout total legado atual é **660 segundos**, configurável de 1 a 660 s,
com conexão limitada a 5 s. Inclui upload/download e streaming não renova o prazo.
O orçamento foi ampliado para dar 60 s de margem ao cliente de lotes de 600 s;
isso não estende o timeout desse cliente. O transporte moderno tem padrão de
30 s, sombra de 2 s, e comandos Oracle novos de 30 s. São limites distintos;
conciliar clientes, gateway, proxies, processo e banco antes do rollout.
A espera completa de 600/660 s e escrita fiscal lenta não foram verificadas;
testes com prazo reduzido demonstram a mecânica, não capacidade por 11 minutos.

| Cenário | Comportamento atual e resposta operacional |
| --- | --- |
| Flags ausentes/inválidas ou kill switch ativo | Novas decisões vão ao legado. Verificar arquivo efetivamente montado e todas as réplicas; o legado precisa estar disponível. |
| Moderno indisponível/lento em `novo` | 502 de transporte ou 504 de timeout, sem replay. Se a resposta já começou, conexão abortada. Retornar chamadas seguintes ao legado e investigar. |
| Sombra falha, satura ou excede limites | Cliente conserva resposta legada; observação falha ou é perdida. Suspender expansão e avaliar cobertura e carga. Falha da sombra não prova divergência nem paridade. |
| Legado/Oracle indisponível | Sombra depende da resposta legada; kill switch não recupera banco compartilhado nem corrige falha do legado. Acionar sustentação/DBA; não trocar rota para ocultar causa desconhecida. |
| Cliente desconecta ou prazo legado expira | Transporte é cancelado; Oracle pode continuar e confirmar efeito fiscal. Não repetir automaticamente cancelamento, emissão ou lote. |
| Cancelamento confirmou, mas releitura/serialização/envio falhou | O cliente pode ver erro apesar de a nota já estar cancelada; a guia também pode exigir reconciliação. Tratar resultado como ambíguo, sem reenvio cego. |
| Guia inconsistente ou efeito autônomo | Trocar a rota não reverte commits. Isolar novas escritas afetadas e conduzir correção pelo procedimento fiscal aprovado com DBA, preservando evidências. |
| Réplicas com flags diferentes | Coorte pode ir a modos distintos apesar do hash estável. Conter expansão e conferir distribuição/leitura por instância; não presumir atomicidade global. |

Para resultado ambíguo de cancelamento, identificar nota/prestador no procedimento
restrito existente, consultar estado, data/motivo e guia com DBA/responsável fiscal,
verificar trabalho ainda em execução e registrar a conclusão: confirmado, não
executado ou indeterminado. A confirmação de ausência de efeito requer evidência
do término da execução; um SELECT momentâneo ou um timeout não bastam. Se continuar
indeterminado, manter pendência em reconciliação e impedir repetição automática.
Não há endpoint/protocolo novo de resolução implementado.

Runbook de retorno atual: substituir atomicamente o JSON por
`{"KillSwitch":true,"Operations":{}}`; verificar leitura e consultas legadas,
WSDL e saúde em cada instância; aguardar/acompanhar chamadas em andamento e só
então parar o moderno se necessário. Não reenviar requisições que perderam a
resposta. Voltar uma consulta ao legado não altera dados; voltar uma escrita
futura muda apenas o executor das chamadas seguintes. Retirar toda a fachada e
devolver a porta ao legado exige operação separada, podendo interromper conexões.
Não usar remoção de volumes, `down -v` ou limpeza de banco para rollback.

## Pendências para operar fora da réplica local

- **Deployment:** o Compose oferece Oracle, legado, fachada e consulta interna;
  não oferece backend de observabilidade ou manifests Kubernetes. Fachada não tem
  HEALTHCHECK Compose; seus endpoints HTTP existem. Validar recursos, probes,
  drenagem de conexões longas, imagens efetivas, TLS e estratégia de implantação
  antes de tráfego real. Recriação de processo pode interromper chamadas.
- **Autenticação e dados:** não há autenticação/autorização nova. Rede interna e
  allowlist de IM não garantem isolamento fiscal entre contribuintes. Resolver
  identidade, acesso por operação, segredos e usuário Oracle de privilégio mínimo;
  a credencial didática não é credencial de produção. Não publicar REST interno
  como portal pronto.
- **Observabilidade:** logs categóricos e health são o que existe. Completar traces
  W3C ponta a ponta, logs correlacionados, métricas por operação/modo, backend,
  alertas e reprodução sanitizada de divergências. A sombra hoje não copia contexto
  da chamada do cliente; não há rastreamento distribuído completo demonstrado.
- **Oracle 19c:** repetir corpus, testes de integração, planos/custo, conversões,
  NLS/timezone, cancelamento do driver e cenários transacionais na versão alvo.
  Oracle 23ai local e build net48 não certificam Oracle 19c ou IIS de produção.
- **Múltiplas réplicas:** arquivo, vagas de sombra e pool são locais. Duas vagas e
  pool máximo 10 por processo podem multiplicar carga conforme réplicas crescem.
  Definir e ensaiar distribuição de flags, conferência por instância, capacidade
  agregada e resposta a partições; não há controlador ou auditoria central hoje.

A aprovação de cada etapa deve registrar evidência, janela/população, limitações,
artefatos e responsáveis. Nenhuma das três operações tem, com o material atual,
aceite de migração integral ou de retirada no monolito.
