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

Timeout total configurável de 1 a 300 segundos (padrão 120), incluindo upload e
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
