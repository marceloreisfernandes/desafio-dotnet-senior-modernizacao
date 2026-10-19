using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
public sealed class HttpContractTests
{
    private const string BasePath = "/api/v1/prestadores/10000001/nfse";
    private const string Dates = "dataInicial=2026-10-01&dataFinal=2026-10-31";

    [DataTestMethod]
    [DataRow("", "periodo-invalido")]
    [DataRow("dataInicial=bad&dataFinal=2026-10-31", "periodo-invalido")]
    [DataRow("dataInicial=2026-10-31&dataFinal=2026-10-01", "periodo-invalido")]
    [DataRow("dataInicial=2026-10-01&dataFinal=2026-11-01", "periodo-invalido")]
    [DataRow("dataInicial=10/01/2026&dataFinal=10/31/2026", "periodo-invalido")]
    [DataRow("dataInicial=2026-10-01T00:00:00&dataFinal=2026-10-31", "periodo-invalido")]
    [DataRow("dataInicial=9999-12-31&dataFinal=9999-12-31", "periodo-invalido")]
    [DataRow(Dates + "&pagina=0", "pagina-invalida")]
    [DataRow(Dates + "&pagina=-1", "pagina-invalida")]
    [DataRow(Dates + "&pagina=abc", "pagina-invalida")]
    [DataRow(Dates + "&pagina=2147483648", "pagina-invalida")]
    [DataRow(Dates + "&pagina=1000001", "pagina-invalida")]
    [DataRow(Dates + "&pagina=", "pagina-invalida")]
    [DataRow(Dates + "&documentoTomador=01234567000194", "documento-invalido")]
    [DataRow(Dates + "&documentoTomador=01.234.567/0001-95", "documento-invalido")]
    [DataRow(Dates + "&documentoTomador=xx01234567000195", "documento-invalido")]
    [DataRow(Dates + "&documentoTomador=", "documento-invalido")]
    [DataRow(Dates + "&pagina=1&pagina=2", "parametros-invalidos")]
    [DataRow(Dates + "&other=value", "parametros-invalidos")]
    public async Task InvalidRestInputHasDocumentedProblemAndDoesNotReadOracle(string query, string code)
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException("Invalid input reached application."));
        using var response = await server.Client.GetAsync(BasePath + "?" + query);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(400, problem.GetProperty("status").GetInt32());
        Assert.AreEqual(code, problem.GetProperty("codigo").GetString());
        Assert.AreEqual("urn:notacarioca:problema:" + code, problem.GetProperty("type").GetString());
    }

    [DataTestMethod]
    [DataRow("1000000")]
    [DataRow("10000001%0A")]
    [DataRow("10000001%27%20OR%201%3D1")]
    public async Task InvalidProviderIdentifier(string im)
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException());
        using var response = await server.Client.GetAsync($"/api/v1/prestadores/{im}/nfse?{Dates}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RestSuccessKeepsZeroesDecimalsStatesAndInclusiveFinalDay()
    {
        await using var server = await HttpServer.Start((query, _) =>
        {
            Assert.AreEqual(new Periodo(new DateTime(2026, 10, 1), new DateTime(2026, 10, 2)), query.Periodo);
            Assert.AreEqual("01234567000195", query.DocumentoTomador);
            Assert.AreEqual(Janela.DaPagina(1), query.Janela);
            return Task.FromResult(new ResultadoConsulta(new PaginaNfse([ApplicationTests.Nota(status: SituacaoNota.Cancelada)], false)));
        });
        using var response = await server.Client.GetAsync(BasePath + "?dataInicial=2026-10-01&dataFinal=2026-10-01&documentoTomador=01234567000195");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var invoice = body.GetProperty("itens")[0];
        Assert.AreEqual("01234567000195", invoice.GetProperty("tomador").GetProperty("documento").GetString());
        Assert.AreEqual(12.52m, invoice.GetProperty("valorIss").GetDecimal());
        Assert.AreEqual("Cancelada", invoice.GetProperty("situacao").GetString());
        Assert.AreEqual(50, body.GetProperty("tamanhoPagina").GetInt32());
    }

    [TestMethod]
    public async Task EmptyPageIsRestSuccessAndSoapE040()
    {
        await using var server = await HttpServer.Start((_, _) => Task.FromResult(new ResultadoConsulta(new PaginaNfse([], false))));
        using var rest = await server.Client.GetAsync(BasePath + "?" + Dates + "&pagina=1000000");
        Assert.AreEqual(HttpStatusCode.OK, rest.StatusCode);
        var body = JsonDocument.Parse(await rest.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(0, body.GetProperty("itens").GetArrayLength());
        using var soap = await SendSoap(server.Client, "<inputXML>&lt;ConsultarNfseEnvio/&gt;</inputXML>");
        Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E040")), await soap.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task MissingProviderHasRest404AndSoapE010()
    {
        await using var server = await HttpServer.Start((_, _) => Task.FromResult(new ResultadoConsulta(Falha: FalhaConsulta.PrestadorInexistente)));
        using var rest = await server.Client.GetAsync(BasePath + "?" + Dates);
        Assert.AreEqual(HttpStatusCode.NotFound, rest.StatusCode);
        using var soap = await SendSoap(server.Client, "<inputXML>&lt;ConsultarNfseEnvio/&gt;</inputXML>");
        Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E010")), await soap.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DependencyTimeoutHasRest503AndSanitizedSoapFault()
    {
        await using var server = await HttpServer.Start((_, _) => throw new TimeoutException("sensitive-connection-and-document"));
        using var rest = await server.Client.GetAsync(BasePath + "?" + Dates);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rest.StatusCode);
        var restBody = await rest.Content.ReadAsStringAsync();
        StringAssert.Contains(restBody, "oracle-indisponivel");
        Assert.IsFalse(restBody.Contains("sensitive"));
        using var soap = await SendSoap(server.Client, "<inputXML>&lt;ConsultarNfseEnvio/&gt;</inputXML>");
        Assert.AreEqual(HttpStatusCode.InternalServerError, soap.StatusCode);
        Assert.AreEqual(SoapAdapter.Fault("soap:Server", "Erro interno no processamento."), await soap.Content.ReadAsStringAsync());
    }

    [DataTestMethod]
    [DataRow("CancelarNfse")]
    [DataRow("RecepcionarLoteRps")]
    [DataRow("ConsultarNfseServicoTomado")]
    [DataRow("")]
    public async Task UnsupportedActionCannotInvokeApplication(string operation)
    {
        // Controlled local service only; never send these actions to the real legacy.
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException());
        using var response = await SendSoap(server.Client, "<inputXML/>", operation);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(SoapAdapter.Fault("soap:Client", "Operação desconhecida: " + operation), await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task MalformedEnvelopeIsClientFaultAndInnerXmlIsBusinessError()
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException());
        using var malformed = await SendSoap(server.Client, "<broken");
        Assert.AreEqual(HttpStatusCode.InternalServerError, malformed.StatusCode);
        Assert.AreEqual(SoapAdapter.Fault("soap:Client", "Envelope SOAP inválido"), await malformed.Content.ReadAsStringAsync());
        using var inner = await SendSoap(server.Client, "<inputXML>&lt;broken</inputXML>");
        Assert.AreEqual(HttpStatusCode.OK, inner.StatusCode);
        Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E001")), await inner.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task OversizedSoapBodyIsDocumented413WithoutCallingApplication()
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException());
        using var response = await SendSoap(server.Client, new string('x', 1_048_577));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("requisicao-grande", body.GetProperty("codigo").GetString());
    }

    [TestMethod]
    public async Task ClientCancellationReachesOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await HttpServer.Start(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            throw new AssertFailedException();
        });
        using var cancellation = new CancellationTokenSource();
        var request = server.Client.GetAsync(BasePath + "?" + Dates, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task WsdlAdvertisesOnlyExtractedReadAndOpenApiDescribesErrors()
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException());
        var wsdl = XDocument.Parse(await server.Client.GetStringAsync("/NfseWS.asmx?wsdl"));
        XNamespace ns = "http://schemas.xmlsoap.org/wsdl/";
        var operations = wsdl.Descendants(ns + "portType").Single().Elements(ns + "operation").ToArray();
        Assert.AreEqual(1, operations.Length);
        Assert.AreEqual("ConsultarNfse", operations[0].Attribute("name")!.Value);
        XNamespace schema = "http://www.w3.org/2001/XMLSchema";
        XNamespace binding = "http://schemas.xmlsoap.org/wsdl/soap/";
        var request = wsdl.Descendants(schema + "element").Single(e => (string?)e.Attribute("name") == "ConsultarNfseRequest");
        var response = wsdl.Descendants(schema + "element").Single(e => (string?)e.Attribute("name") == "ConsultarNfseResponse");
        var input = request.Descendants(schema + "element").Single();
        var output = response.Descendants(schema + "element").Single();
        Assert.AreEqual("inputXML", (string?)input.Attribute("name"));
        Assert.AreEqual("outputXML", (string?)output.Attribute("name"));
        foreach (var element in new[] { input, output })
        {
            Assert.AreEqual("s:string", (string?)element.Attribute("type"));
            Assert.AreEqual("0", (string?)element.Attribute("minOccurs"));
            Assert.AreEqual("1", (string?)element.Attribute("maxOccurs"));
        }
        var soapOperation = wsdl.Descendants(binding + "operation").Single();
        Assert.AreEqual(SoapAdapter.Namespace + "ConsultarNfse", (string?)soapOperation.Attribute("soapAction"));
        Assert.AreEqual("document", (string?)soapOperation.Attribute("style"));
        var openapi = JsonDocument.Parse(await server.Client.GetStringAsync("/openapi/v1.json")).RootElement;
        Assert.AreEqual("3.0.3", openapi.GetProperty("openapi").GetString());
        var responses = openapi.GetProperty("paths").GetProperty("/api/v1/prestadores/{inscricaoMunicipal}/nfse").GetProperty("get").GetProperty("responses");
        foreach (var code in new[] { "200", "400", "404", "500", "503" }) Assert.IsTrue(responses.TryGetProperty(code, out _));
        Assert.AreEqual("OK", await server.Client.GetStringAsync("/health/live"));
    }

    internal static Task<HttpResponseMessage> SendSoap(HttpClient client, string envelope, string operation = "ConsultarNfse")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", '"' + SoapAdapter.Namespace + operation + '"');
        return client.SendAsync(request);
    }
}
