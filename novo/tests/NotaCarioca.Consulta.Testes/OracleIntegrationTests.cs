using System.Data;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
[TestCategory("Oracle")]
public sealed class OracleIntegrationTests
{
    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("NFSE_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(value)) Assert.Inconclusive("Set NFSE_TEST_CONNECTION_STRING to run real read-only Oracle integration.");
        return value!;
    }

    [TestMethod]
    public async Task ReadOnlyStatementUnderAutocommitDoesNotRetainTransaction()
    {
        // No DML, DDL, SELECT FOR UPDATE or transaction-ID allocation.
        await using var connection = new OracleConnection(ConnectionString());
        await connection.OpenAsync();
        Assert.IsTrue(connection.AutoCommit);
        using var start = new OracleCommand("SET TRANSACTION READ ONLY", connection);
        await start.ExecuteNonQueryAsync();
        using var probe = new OracleCommand("SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL", connection);
        var id = await probe.ExecuteScalarAsync();
        Console.WriteLine($"autocommit={connection.AutoCommit}; active={connection.IsInTransaction}; local_id={id}");
        Assert.IsFalse(connection.IsInTransaction);
        Assert.IsTrue(id is null or DBNull);

        // Positive control: turn off provider autocommit before SET TRANSACTION.
        connection.AutoCommit = false;
        await start.ExecuteNonQueryAsync();
        id = await probe.ExecuteScalarAsync();
        Console.WriteLine($"autocommit={connection.AutoCommit}; active={connection.IsInTransaction}; local_id={id}");
        Assert.IsTrue(connection.IsInTransaction);
        // A read-only transaction need not allocate an ID. Explicitly end the control.
        connection.Rollback();
    }

    private static string Hash(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    [TestMethod]
    public async Task FortyGoldenCasesMatchBaselineAndOriginalHostExactly()
    {
        var oracle = new OracleConsulta(ConnectionString());
        var legacyUrl = Environment.GetEnvironmentVariable("NFSE_LEGACY_BASE_URL");
        if (string.IsNullOrWhiteSpace(legacyUrl)) Assert.Inconclusive("Set NFSE_LEGACY_BASE_URL to run original-host parity.");
        using var legacy = new HttpClient { BaseAddress = new(legacyUrl!), Timeout = TimeSpan.FromSeconds(45) };
        await using var server = await HttpServer.Start(oracle.ExecutarAsync);
        var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cases.json")));
        var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "baseline.json")));
        var hashes = baseline.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("case").GetString()!, row => row.GetProperty("legacy").GetProperty("sha256").GetString()!);
        var count = 0;
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var payload = Payload(item);
            using var original = await HttpContractTests.SendSoap(legacy, payload);
            using var modern = await HttpContractTests.SendSoap(server.Client, payload);
            var originalHash = Hash(await original.Content.ReadAsByteArrayAsync());
            var modernHash = Hash(await modern.Content.ReadAsByteArrayAsync());
            Assert.AreEqual(hashes[name], originalHash, "Legacy baseline changed: " + name);
            Assert.AreEqual(original.StatusCode, modern.StatusCode, name);
            Assert.AreEqual(original.Content.Headers.ContentType!.ToString(), modern.Content.Headers.ContentType!.ToString(), name);
            Assert.AreEqual(originalHash, modernHash, "SOAP bytes differ: " + name);
            count++;
        }
        Assert.AreEqual(40, count);
    }

    [TestMethod]
    public async Task ReviewRecipientParityAndStrictXmlDivergencesAgainstOriginalHost()
    {
        var oracle = new OracleConsulta(ConnectionString());
        var legacyUrl = Environment.GetEnvironmentVariable("NFSE_LEGACY_BASE_URL");
        if (string.IsNullOrWhiteSpace(legacyUrl)) Assert.Inconclusive("Set NFSE_LEGACY_BASE_URL.");
        using var legacy = new HttpClient { BaseAddress = new(legacyUrl!) };
        await using var server = await HttpServer.Start(oracle.ExecutarAsync);
        using var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "review-cases.json")));
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var payload = Payload(item);
            using var original = await HttpContractTests.SendSoap(legacy, payload);
            using var modern = await HttpContractTests.SendSoap(server.Client, payload);
            Assert.AreEqual(HttpStatusCode.OK, original.StatusCode);
            Assert.AreEqual(original.StatusCode, modern.StatusCode);
            Assert.AreEqual(original.Content.Headers.ContentType!.ToString(), modern.Content.Headers.ContentType!.ToString());
            var originalBody = await original.Content.ReadAsStringAsync();
            var modernBody = await modern.Content.ReadAsStringAsync();
            if (item.TryGetProperty("divergence", out _))
            {
                var output = XDocument.Parse(originalBody).Descendants(XName.Get("outputXML", SoapAdapter.Namespace)).Single().Value;
                Assert.AreEqual(3, XDocument.Parse(output).Descendants("CompNfse").Count());
                Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E001")), modernBody);
            }
            else Assert.AreEqual(originalBody, modernBody, item.GetProperty("name").GetString());
        }
    }

    private static string Payload(JsonElement item)
    {
        if (item.TryGetProperty("envelope", out var envelope)) return envelope.GetString()!;
        var file = item.TryGetProperty("example", out var example) ? example.GetString()! : "04-consultar-nfse.xml";
        var inner = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "examples", file))
            .Descendants(XName.Get("inputXML", SoapAdapter.Namespace)).Single().Value;
        foreach (var replacement in item.TryGetProperty("replace", out var replacements) ? replacements.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var before = replacement[0].GetString()!;
            var after = replacement[1].GetString()!;
            var position = inner.IndexOf(before, StringComparison.Ordinal);
            Assert.IsTrue(position >= 0);
            inner = string.Concat(inner.AsSpan(0, position), after, inner.AsSpan(position + before.Length));
        }
        if (item.TryGetProperty("input", out var input)) inner = input.GetString()!;
        return $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<ConsultarNfseRequest xmlns=\"{SoapAdapter.Namespace}\"><inputXML>{SecurityElement.Escape(inner)}</inputXML>" +
            "</ConsultarNfseRequest></soap:Body></soap:Envelope>";
    }

    [TestMethod]
    public async Task OracleRowsAreSourceOfTruthAndRestIncludesFinalDay()
    {
        var oracle = new OracleConsulta(ConnectionString());
        var request = ApplicationTests.Request() with
        {
            Periodo = new(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), new DateTime(2026, 10, 1))
        };
        var result = await oracle.ExecutarAsync(request, default);
        Assert.IsNull(result.Falha);
        Assert.AreEqual(11, result.Pagina!.Itens.Count);
        await using var connection = new OracleConnection(ConnectionString());
        await connection.OpenAsync();
        using var command = new OracleCommand("SELECT VALOR_ISS FROM NFSE WHERE PRESTADOR_ID=:prest AND NUMERO=:num", connection) { BindByName = true };
        command.Parameters.Add("prest", OracleDbType.Int64, 1, ParameterDirection.Input);
        command.Parameters.Add("num", OracleDbType.Int64, result.Pagina.Itens[0].Numero, ParameterDirection.Input);
        var stored = decimal.Parse((await command.ExecuteScalarAsync())!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(stored, result.Pagina.Itens[0].ValorIss);
        Assert.IsTrue(result.Pagina.Itens.Any(n => n.ValorServicos == 250.50m && n.ValorIss == 12.52m));
        await using var server = await HttpServer.Start(oracle.ExecutarAsync);
        using var rest = await server.Client.GetAsync("/api/v1/prestadores/10000001/nfse?dataInicial=2026-10-02&dataFinal=2026-10-02");
        Assert.AreEqual(HttpStatusCode.OK, rest.StatusCode);
        var body = JsonDocument.Parse(await rest.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(1, body.GetProperty("itens").GetArrayLength());
        Assert.AreEqual("03703701000185", body.GetProperty("itens")[0].GetProperty("tomador").GetProperty("documento").GetString());
    }

    [TestMethod]
    public async Task OracleParametersCannotBroadenProviderOrRecipientQuery()
    {
        var oracle = new OracleConsulta(ConnectionString());
        var provider = await oracle.ExecutarAsync(ApplicationTests.Request() with { InscricaoMunicipal = "10000001' OR '1'='1" }, default);
        Assert.AreEqual(FalhaConsulta.PrestadorInexistente, provider.Falha);
        var document = await oracle.ExecutarAsync(ApplicationTests.Request("01234567000195' OR '1'='1"), default);
        Assert.AreEqual(FalhaConsulta.DocumentoInvalido, document.Falha);
        var filtered = await oracle.ExecutarAsync(ApplicationTests.Request("01234567000195"), default);
        Assert.AreEqual(1, filtered.Pagina!.Itens.Count);
        Assert.IsTrue(filtered.Pagina.Itens.All(n => n.Tomador.Documento == "01234567000195"));
    }

    [TestMethod]
    public async Task OracleEmptyPagesOverflowAndPooledReuse()
    {
        var oracle = new OracleConsulta(ConnectionString());
        foreach (var page in new[] { 2, 1_000_000, int.MaxValue })
        {
            var result = await oracle.ExecutarAsync(ApplicationTests.Request() with { Janela = Janela.DaPagina(page) }, default);
            Assert.AreEqual(0, result.Pagina!.Itens.Count);
            Assert.IsFalse(result.Pagina.ProximaPagina);
        }
        for (var i = 0; i < 3; i++)
        {
            var result = await oracle.ExecutarAsync(ApplicationTests.Request(), default);
            Assert.AreEqual(3, result.Pagina!.Itens.Count);
        }
    }

    [TestMethod]
    public async Task InternalDtdDivergenceIsMeasuredAgainstOriginal()
    {
        var oracle = new OracleConsulta(ConnectionString());
        var legacyUrl = Environment.GetEnvironmentVariable("NFSE_LEGACY_BASE_URL");
        if (string.IsNullOrWhiteSpace(legacyUrl)) Assert.Inconclusive("Set NFSE_LEGACY_BASE_URL.");
        using var legacy = new HttpClient { BaseAddress = new(legacyUrl!) };
        await using var modern = await HttpServer.Start(oracle.ExecutarAsync);
        const string inner = "<!DOCTYPE ConsultarNfseEnvio [<!ENTITY im '10000001'>]>" +
            "<ConsultarNfseEnvio><Prestador><InscricaoMunicipal>&im;</InscricaoMunicipal></Prestador>" +
            "<PeriodoEmissao><DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao></ConsultarNfseEnvio>";
        var payload = "<inputXML>" + SecurityElement.Escape(inner) + "</inputXML>";
        using var original = await HttpContractTests.SendSoap(legacy, payload);
        using var response = await HttpContractTests.SendSoap(modern.Client, payload);
        Assert.AreEqual(HttpStatusCode.OK, original.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = XDocument.Parse(await original.Content.ReadAsStringAsync()).Descendants(XName.Get("outputXML", SoapAdapter.Namespace)).Single().Value;
        Assert.AreEqual(3, XDocument.Parse(output).Descendants("CompNfse").Count());
        Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E001")), await response.Content.ReadAsStringAsync());
    }
}
