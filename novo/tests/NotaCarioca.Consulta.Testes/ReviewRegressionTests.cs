using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Servicos;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
public sealed class ReviewRegressionTests
{
    [DataTestMethod]
    [DataRow("<Tomador><CpfCnpj><Cpf>52998224725</Cpf></CpfCnpj></Tomador>", "52998224725")]
    [DataRow("<Tomador/>", null)]
    [DataRow("<Tomador><CpfCnpj><Cnpj> </Cnpj><Cpf>52998224725</Cpf></CpfCnpj></Tomador>", "52998224725")]
    [DataRow("<Tomador><CpfCnpj><Cnpj>01234567000195</Cnpj><Cpf>52998224725</Cpf></CpfCnpj></Tomador>", "01234567000195")]
    public async Task OnlyFirstRecipientAndLegacyDocumentPrecedence(string first, string? expected)
    {
        var input = "<ConsultarNfseEnvio>" + first +
            "<Tomador><CpfCnpj><Cnpj>11100003000113</Cnpj></CpfCnpj></Tomador></ConsultarNfseEnvio>";
        var legacy = LeitorLoteRps.Carregar(input).DocumentElement!.SelectSingleNode("Tomador")!;
        var document = LeitorLoteRps.Texto(legacy, "CpfCnpj/Cnpj");
        if (string.IsNullOrEmpty(document)) document = LeitorLoteRps.Texto(legacy, "CpfCnpj/Cpf");
        Assert.AreEqual(expected, document);
        var adapter = new SoapAdapter((query, _) =>
        {
            Assert.AreEqual(expected, query.DocumentoTomador);
            return Task.FromResult(new ResultadoConsulta(new PaginaNfse([], false)));
        });
        Assert.AreEqual(SoapAdapter.Erro("E040"), await adapter.ConsultarAsync(input, default));
    }

    [DataTestMethod]
    [DataRow("<!DOCTYPE ConsultarNfseEnvio><ConsultarNfseEnvio/>")]
    [DataRow("<ConsultarNfseEnvio><Prestador><InscricaoMunicipal>&#1;</InscricaoMunicipal></Prestador></ConsultarNfseEnvio>")]
    public async Task StrictParserDivergencesAreDeliberate(string input)
    {
        Assert.IsNotNull(LeitorLoteRps.Carregar(input).DocumentElement);
        var adapter = new SoapAdapter((_, _) => throw new AssertFailedException("Unsafe XML reached application."));
        Assert.AreEqual(SoapAdapter.Erro("E001"), await adapter.ConsultarAsync(input, default));
    }

    [DataTestMethod]
    [DataRow(64, true)]
    [DataRow(65, false)]
    [DataRow(4096, false)]
    public async Task DepthIsBoundedBeforeInnerText(int depth, bool accepted)
    {
        var nested = string.Concat(Enumerable.Repeat("<x>", depth - 2)) + "10000001" +
            string.Concat(Enumerable.Repeat("</x>", depth - 2));
        var input = "<ConsultarNfseEnvio><Pagina>" + nested + "</Pagina></ConsultarNfseEnvio>";
        var calls = 0;
        var adapter = new SoapAdapter((_, _) =>
        {
            calls++;
            return Task.FromResult(new ResultadoConsulta(new PaginaNfse([], false)));
        });
        Assert.AreEqual(SoapAdapter.Erro(accepted ? "E040" : "E001"), await adapter.ConsultarAsync(input, default));
        Assert.AreEqual(accepted ? 1 : 0, calls);
        if (accepted) Assert.AreEqual("ok", SoapAdapter.LerInput("<inputXML>ok</inputXML>"));
        else Assert.ThrowsException<XmlException>(() => SoapAdapter.LerInput(input));
    }

    [TestMethod]
    public async Task ExcessiveDepthKeepsDocumentedSoapErrorsOverHttp()
    {
        await using var server = await HttpServer.Start((_, _) => throw new AssertFailedException("Deep XML reached application."));
        var deep = string.Concat(Enumerable.Repeat("<x>", 65)) + "value" +
            string.Concat(Enumerable.Repeat("</x>", 65));
        var input = "<ConsultarNfseEnvio><Pagina>" + deep + "</Pagina></ConsultarNfseEnvio>";
        using var inner = await HttpContractTests.SendSoap(server.Client, "<inputXML>" + System.Security.SecurityElement.Escape(input) + "</inputXML>");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, inner.StatusCode);
        Assert.AreEqual(SoapAdapter.Envelope(SoapAdapter.Erro("E001")), await inner.Content.ReadAsStringAsync());
        using var outer = await HttpContractTests.SendSoap(server.Client, deep);
        Assert.AreEqual(System.Net.HttpStatusCode.InternalServerError, outer.StatusCode);
        Assert.AreEqual(SoapAdapter.Fault("soap:Client", "Envelope SOAP inválido"), await outer.Content.ReadAsStringAsync());
    }

    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(49, false)]
    [DataRow(50, false)]
    [DataRow(51, true)]
    [DataRow(52, true)]
    public async Task ExtraRowIsNeverDecoded(int rows, bool next)
    {
        var reads = 0;
        var decodes = 0;
        var page = await OracleConsulta.LerPaginaAsync(() => Task.FromResult(++reads <= rows), () =>
        {
            Assert.IsTrue(++decodes <= 50, "The extra row must not be decoded.");
            return ApplicationTests.Nota(decodes);
        });
        Assert.AreEqual(Math.Min(rows, 50), decodes);
        Assert.AreEqual(decodes, page.Itens.Count);
        Assert.AreEqual(Math.Min(rows + 1, 51), reads);
        Assert.AreEqual(next, page.ProximaPagina);
    }
}
