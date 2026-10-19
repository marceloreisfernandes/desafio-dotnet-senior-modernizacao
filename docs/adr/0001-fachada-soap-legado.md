# ADR 0001 — Primeiro incremento: fachada HTTP/SOAP para o legado

Status: implementado; consultar o relatório de verificação para evidências de execução.

## Contexto e decisão

Introduzir um ponto de entrada .NET 8 separado, sem extrair regras ou operações.
Todas as requisições para `/NfseWS.asmx`, inclusive ações desconhecidas e métodos
não suportados pelo legado, são encaminhadas ao host existente. A decisão de
negócio, os SOAP Faults e o acesso ao Oracle continuam pertencendo ao legado.

Fluxo: cliente → localhost:8080 → fachada → webservices:8080 → Oracle existente.
Somente a fachada publica a porta SOAP no host. O serviço Oracle, seus mounts,
volume nomeado e configuração permanecem iguais. A fachada não referencia
projetos legados nem possui conexão com o banco.

Usar um cliente nomeado de `IHttpClientFactory`, sem novas dependências de
produção. Copiar streams de forma assíncrona; não interpretar, normalizar ou
serializar envelopes. Preservar método, query string, SOAPAction, headers de
conteúdo e de aplicação, status e corpo. Headers de conexão, inclusive os
indicados dinamicamente em `Connection`, são removidos nos dois sentidos;
`Host` é produzido pelo transporte para o upstream configurado. O enquadramento
HTTP (por exemplo, chunking) pode mudar, mas os bytes SOAP não.

Não seguir redirecionamentos, compartilhar cookies ou configurar retries.
Cookies recebidos são encaminhados como headers, sem CookieContainer. Uma falha
após envio de um POST pode deixar o resultado do negócio desconhecido; a fachada
nunca repete a operação para tentar ocultar essa incerteza.

Timeout total configurável de 1 a 660 segundos (padrão 660), incluindo upload e
download; conexão limitada a 5 segundos. Cancelamento do cliente interrompe a
chamada ao upstream. Falha de comunicação retorna HTTP 502, timeout retorna 504,
com texto genérico. Depois que headers/corpo já foram enviados, abortar a conexão
para impedir que uma resposta incompleta pareça íntegra. Respostas HTTP do legado,
inclusive 4xx, 5xx e SOAP Faults, passam sem conversão.

O único documento XML interpretado é o WSDL de um GET bem-sucedido com `?wsdl`.
Reescrever exclusivamente `soap:address/@location` para `Legacy:PublicServiceUrl`,
sem confiar em Host ou X-Forwarded-Host do cliente. Preservar o contrato XML,
incluindo todas as operações; a serialização e a declaração XML podem mudar.
Descartar validadores/length antigos e requisições condicionais/range para esse
documento. Solicitar WSDL sem compressão; compressão inesperada, XML inválido,
DTD ou documento acima de 4 MiB / 2 Mi caracteres falham explicitamente com 502.

`/health/live` verifica o processo. `/health` e `/health/ready` encaminham o health
legado, que consulta Oracle; preservam seu status e corpo. O Compose espera o
processo legado iniciar, e readiness pode falhar enquanto ele ainda não atende.

Logs próprios contêm apenas o código da falha; logs HTTP informativos do framework
e do cliente são filtrados para evitar queries e headers sensíveis. Não habilitar
logging de corpos. O logging preexistente do legado não é alterado neste incremento.

## Alternativas e limites

YARP seria apropriado para roteamento mais complexo, mas acrescentaria dependência
e configuração desnecessárias neste passo. Um cliente SOAP gerado exigiria interpretar
e serializar o contrato, ampliando o risco de incompatibilidade. Incluir o proxy no
host legado reduziria o isolamento e a reversibilidade. Nenhuma destas alternativas
foi adotada.

Sem serviço novo, sombra, percentuais, roteamento dinâmico, REST, CDC ou mudanças
de PL/SQL. Não há certificação de produção, TLS, throughput ou falhas reais do Oracle.
Streaming não limita memória pelo tamanho do SOAP, mas o tamanho máximo da requisição
continua sem limite como no legado; controles de capacidade ficam para outro passo.

Referências: [IHttpClientFactory](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory)
e [RFC 9110, Connection](https://www.rfc-editor.org/rfc/rfc9110.html#section-7.6.1).

## Incremento seguinte — roteamento Strangler Fig em runtime

Esta seção substitui as restrições do primeiro incremento sobre ausência de
parsing, serviço novo, sombra e flags. O proxy/streaming e a consulta independente
são preservados; não há novas dependências de produção, conexão Oracle na fachada,
objetos de banco ou mudança de contrato WSDL. A extração segue a ADR 0002.

A fachada admite apenas a ação canônica ConsultarNfse com envelope/wrapper/IM
inequívocos e corpo limitado. Todas as demais operações e entradas não elegíveis
ficam opacas no legado, inclusive ações desconhecidas e todas as escritas.
A IM declarada é metadado de seleção, nunca autenticação/autorização.

Decisão: arquivo JSON local obrigatório para habilitar migração, com kill switch,
regra por operação, allowlist de IM e percentual determinístico por SHA-256 da
operação/IM. Sem configuração válida, LEGACY. Ler snapshot limitado a 16 KiB a
cada decisão e novamente após upload/inspeção; não manter último estado permissivo.
Operadores substituem o arquivo atomicamente no diretório montado somente para
leitura no processo. Não há endpoint de gestão, banco/Redis ou serviço de flags.
A propagação é na próxima leitura, sem polling/restart/deploy, sujeita à latência
do filesystem local; não coordena múltiplas réplicas.

LEGACY usa o proxy original. NEW usa o mesmo transporte, com origem moderna fixa
e uma tentativa, sem replay para legado em falhas ou resultados ambíguos. O kill
switch retorna chamadas seguintes ao legado; chamadas já despachadas terminam.
O WSDL e health/ready continuam no legado. Não disponibilizar o WSDL parcial novo
como contrato público.

SHADOW transmite integralmente a resposta legada e, após sua cópia, agenda somente
a leitura moderna. Reserva não bloqueante inclui o tempo no legado, canal e número
de consumidores são limitados (padrão 2), deadline próprio 2 s, buffers apenas em
memória (requisição até 1 MiB, respostas até 2 MiB). Saturação descarta observações;
erros/cancelamento/limites são observados sem afetar o cliente. Não há retries,
tarefas ilimitadas ou disco para payloads. A sombra não copia credenciais/cookies.
Cancelamento HTTP não garante interrupção imediata do SELECT remoto.

Comparar status/content type/bytes sem guardar identificadores ou payloads. Logs
próprios têm somente categorias fixas. Paridade exata, divergência inesperada e
rejeição estrita documentada de referência a controle XML (legado com notas vs
novo E001 exato) são categorias distintas. Não normalizar diferenças fiscais ou
mascarar erros internos como esperados. DTD/profundidade não seguros para obter
metadados continuam no legado. Sombra duplica SELECTs e pode aumentar carga;
consistência por instrução não garante snapshot comum entre serviços.

Reversão: kill switch, conferir leitura/health legados, então parar moderno se
necessário. Sem rollback de dados. O limite legado passa de 120 s (teto 300 s)
para 660 s, com 60 s de margem sobre o cliente de lotes de 600 s. Mantém-se um
prazo total finito, sem infraestrutura por operação. Streaming não renova o prazo;
clientes podem cancelar antes e o transporte propaga esse cancelamento. O legado
original não tinha limite total equivalente: um 504/desconexão não prova ausência
de efeito fiscal, não garante cancelamento Oracle e nunca autoriza retry automático.
Limites de produção incluem credencial Oracle de desenvolvimento privilegiada,
ausência de autorização nova, métricas/auditoria central, coordenação de réplicas,
certificação de carga e interrupção garantida de trabalho remoto.
Formato, procedimento operacional e testes reproduzíveis: [README da fachada](../../fachada/README.md).
