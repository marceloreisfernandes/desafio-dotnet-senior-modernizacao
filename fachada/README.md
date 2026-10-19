# Fachada SOAP — primeiro incremento

Aplicação isolada em .NET 8. Todas as operações continuam no legado.
Decisões e limites: [ADR 0001](../docs/adr/0001-fachada-soap-legado.md).

## Compilar e testar

```bash
dotnet build fachada/src/NotaCarioca.Fachada -c Release
dotnet test fachada/tests/NotaCarioca.Fachada.Testes -c Release
dotnet build legado/NotaCarioca.sln -c Release
dotnet test legado/tests/NotaCarioca.Testes -c Release -f net8.0 --no-build
docker compose config --quiet
docker compose build fachada
```

Os testes da fachada usam Kestrel e upstream controlado em portas de loopback
efêmeras. Não acessam Oracle. Verificam fidelidade dos exemplos existentes, bytes
opacos, headers, status de erro, WSDL, health, timeout e desconexões.

## Operação local

Em uma instalação existente, após aprovação da interrupção breve do endpoint
legado para retirar sua porta pública:

```bash
docker compose up -d --no-deps webservices
docker compose up -d --no-deps fachada
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
curl --fail 'http://localhost:8080/NfseWS.asmx?wsdl'
```

Não executar `compose down`, `down -v` ou `just down`: o último comando já existente
no repositório também remove volumes. Os comandos acima não recriam o Oracle.
Não executar o runner geral `tools/exemplos/executar.sh`: ele inclui escritas.

Para comparar somente as duas consultas autorizadas com o Oracle local, obtenha
o IP interno atual de `webservices` por `docker inspect` e execute:

```bash
python3 tools/verificar-fachada.py --legacy http://IP_INTERNO_ATUAL:8080
```

O script exige notas em ambas as consultas e compara corpos byte a byte, status e
content type; valida health e o contrato WSDL. Imprime somente hashes, tamanhos e
contagens. Precisa de acesso à bridge Docker no host. Não emite nem cancela notas,
não salva respostas com dados pessoais, não altera os exemplos versionados.

Configuração via variáveis de ambiente:

| Variável | Padrão | Finalidade |
| --- | --- | --- |
| `Legacy__BaseUrl` | `http://webservices:8080` | Origem HTTP(S) interna fixa |
| `Legacy__PublicServiceUrl` | `http://localhost:8080/NfseWS.asmx` | Endereço anunciado no WSDL |
| `Legacy__TimeoutSeconds` | `120` | Limite total de 1 a 300 segundos |

Em outro domínio ou HTTPS, alterar explicitamente o endereço público. A aplicação
falha no startup para configuração inválida. `/health` mantém o significado do
health legado; `/health/ready` é um alias e `/health/live` independe de Oracle.

## Rollback

Requer aprovação operacional: haverá interrupção breve na troca da porta.
Parar somente `fachada` com `docker compose stop fachada`. No Compose, devolver
`ports: ["127.0.0.1:8080:8080"]` a `webservices` no lugar de `expose` e remover o
serviço `fachada`. Executar `docker compose up -d --no-deps webservices` e conferir
`/health`, WSDL e as duas consultas. Não tocar em Oracle, volumes, redes ou recursos
de outros projetos. Não é necessário rollback de dados: a fachada não os migra.
