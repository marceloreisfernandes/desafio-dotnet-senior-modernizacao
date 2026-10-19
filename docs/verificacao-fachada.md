# Verificação local — fachada SOAP

Execução: 2026-10-19. Evidência local, sem certificação de produção.

## Git e escopo

Identidade: `tetelbootcamp <tetel.player@gmail.com>`.
Branch: `feat/fachada-soap-legado`, criada da `main` limpa em
`9b9a0a9762f0aea8b2d5d8e7957f36d8f3631ae2`, igual à referência local
`origin/main` no preflight. Não houve fetch, commit, push ou criação de PR.

Alterados: `README.md`, `docker-compose.yml`, `.github/workflows/ci.yml`.
Criados: aplicação e testes sob `fachada/`, seu Dockerfile e README,
`docs/adr/0001-fachada-soap-legado.md`, este relatório e
`tools/verificar-fachada.py`.

`git diff --exit-code -- legado db tools/exemplos Dockerfile` retornou 0:
nenhuma alteração nesses arquivos. Nenhum script SQL, emissão ou cancelamento
foi executado para a verificação. Os únicos POSTs ao ambiente real foram as duas
consultas existentes, enviadas diretamente ao legado e pela fachada.

## Comandos e resultados

SDK local: .NET `8.0.131`; runtime `8.0.31`; VSTest `17.8.0`.

| Comando | Resultado final |
| --- | --- |
| `dotnet build legado/NotaCarioca.sln -c Release` | exit 0; net48 + net8.0; 0 warnings, 0 erros |
| `dotnet test legado/tests/NotaCarioca.Testes -c Release -f net8.0 --no-build` | exit 0; 16 passaram, 1 ignorado, 0 falhas |
| `dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release --no-restore --logger 'console;verbosity=normal'` | exit 0; 23 passaram; 13,7962 s |
| `docker compose config --quiet` | exit 0 |
| `docker compose build fachada` | exit 0; build/publish Release da aplicação |
| `docker compose up -d --no-deps webservices fachada` | exit 0; troca de entrada pública aprovada |
| `docker compose up -d --no-deps fachada` | exit 0; atualização aprovada para a imagem final |
| `python3 tools/verificar-fachada.py --legacy http://192.168.0.3:8080` | exit 0; repetido após atualização final |
| `git diff --check` | exit 0 |

Ocorrências intermediárias: o sandbox bloqueou NuGet e a primeira compilação
restrita do legado terminou sem diagnóstico útil; ambos foram repetidos com
acesso autorizado e passaram. O primeiro build dos testes encontrou caminho
relativo incorreto dos exemplos, corrigido antes da primeira suíte aprovada.
A ampliação de 19 para 23 testes teve uma execução com 22 sucessos e 1 timeout
no primeiro teste de fidelidade durante build Docker simultâneo. O fixture
usava 1 segundo para todos os testes; passou a usar 15 segundos para os casos
normais, mantendo 1 segundo somente nos três testes específicos de timeout.
A suíte final passou integralmente. O timeout de produção permanece 120 segundos.

O único teste ignorado do legado foi `Parse_MesPorExtenso`, preexistente.

## Comparação SOAP com Oracle local

Requisições: exemplos versionados `04-consultar-nfse.xml` e
`05-consultar-nfse-servico-tomado.xml`. Nenhuma normalização de XML ou exclusão
de campos na comparação: igualdade exata de status, content type e bytes.

| Operação | HTTP | Content type | Bytes de resposta | Notas | Igualdade |
| --- | --- | --- | --- | --- | --- |
| ConsultarNfse | 200 | text/xml; charset=utf-8 | 4361 | 3 | exata |
| ConsultarNfseServicoTomado | 200 | text/xml; charset=utf-8 | 1763 | 1 | exata |

SHA-256 das requisições:

```text
ConsultarNfse
ed57e08324f299bb6663ec94608fdd5f31c3b20048f540490b044d2e45e2f6ac
ConsultarNfseServicoTomado
60738d1e2a54c9611d792aa70bf64417b4adde0598158d2f1067d3a4b6e3299b
```

SHA-256 das respostas, iguais no legado e na fachada:

```text
ConsultarNfse
926d073e52cc26d9b3341075bf275567c40b9121468a94fe2b444357aef7f735
ConsultarNfseServicoTomado
2191dcaffd315f37e8fa82c1d5bc8d115c694555b02341ccdb68f07b35a667bd
```

`/health`, `/health/ready` e `/health/live`: HTTP 200, corpo `OK`.
GET `/NfseWS.asmx?wsdl`: HTTP 200; comparação estrutural do WSDL aprovada,
com `soap:address` igual a `http://localhost:8080/NfseWS.asmx`. O teste automatizado
também usa origem privada e endereço público diferentes e Host não confiável.
As respostas reais ficaram apenas na memória do verificador, sem arquivos de payload.

## Docker e Oracle

Estado final: fachada em `127.0.0.1:8080`; legado em `webservices:8080`, sem porta
publicada no host. Oracle conserva a publicação preexistente em `127.0.0.1:1521`.

Oracle antes e depois:

```text
container: f2ee102a0fa927318735116a972ddc86f9377f2f45be34ef93567191c75bb6c3
StartedAt: 2026-10-19T12:48:37.11133376Z
health: healthy
volume: desafio-dotnet-senior-modernizacao_oracle-data
mount: /opt/oracle/oradata
```

Nenhum restart/recreate do Oracle; mounts de scripts continuam somente leitura.
Os mesmos 11 volumes e 17 networks foram encontrados antes e depois. Os seis
containers de outros projetos conservaram IDs e estados. O legado foi recriado
para retirar a porta e a fachada foi criada e atualizada mediante aprovação.
Não houve `down`, prune, exclusão de volume ou alterações nos recursos alheios.

Imagem final da fachada em execução:
`sha256:1b1b7632781f55d37b5c1e6245e918c71ddee556f4f84dff201b3a6ffee35e5a`.
Usuário do container: `1654`, sem root.

O Docker avisou que o kernel não suporta limite de swap; a configuração de
memória do legado foi preservada. Não foi alterada a configuração do host.

## Implementado, verificado e limites

Implementado: encaminhamento de todas as ações sem discriminar operação,
streams opacos, IHttpClientFactory, sem retry configurado, timeout total,
cancelamento de transporte, falhas 502/504, WSDL público e health endpoints.

Verificado com upstream controlado: fidelidade de método/query/headers/corpos,
status 4xx/5xx, cookies e headers de conexão, redirecionamento sem repetição,
cancelamento do cliente, timeout antes e depois dos headers, WSDL lento/inválido,
resposta SOAP comprimida e configuração inválida. Não foi executada nenhuma
operação SOAP de emissão ou cancelamento, nem mesmo para teste de negócio.

Verificado com legado e Oracle reais: somente as duas consultas, os GETs de
health e WSDL, além do inventário de containers/volumes/redes. Não houve auditoria
integral dos dados nem prova de imutabilidade de cada linha do banco; o legado
mantém seus processos internos preexistentes. A verificação não introduziu escritas.

Não verificado: produção, TLS, clientes ERP externos, carga, todos os cenários
de erro do Oracle ou execução real das demais operações SOAP. O roteamento dessas
operações está implementado genericamente; sua execução real foi deliberadamente
excluída. O CI foi estendido, mas não executado no GitHub, pois não houve push.

Rollback operacional e configuração: [fachada/README.md](../fachada/README.md).
Permanece necessária aprovação específica para commit/publicação ou qualquer
próxima fase; nenhum serviço novo, sombra ou roteamento dinâmico foi iniciado.
