using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Servicos;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
public sealed class SoapTests
{
    [DataTestMethod]
    [DataRow("N", SituacaoNota.Normal, "01234567000195")]
    [DataRow("C", SituacaoNota.Cancelada, "01234567000195")]
    [DataRow("S", SituacaoNota.Substituida, "01234567000195")]
    [DataRow("N", SituacaoNota.Normal, "01234567890")]
    public void SerializerMatchesLegacyIncludingEscapesStatusesDocumentsAndStoredTax(string legacyStatus, SituacaoNota status, string document)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.AreEqual(XmlNfseBuilder.CompNfse(LegacyCharacterizationTests.Row(legacyStatus, document)),
                SoapAdapter.SerializarNota(ApplicationTests.Nota(status: status, document: document)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [DataTestMethod]
    [DataRow("E001")]
    [DataRow("E010")]
    [DataRow("E020")]
    [DataRow("E030")]
    [DataRow("E040")]
    public void ErrorBytesMatchOriginal(string code) =>
        Assert.AreEqual(XmlNfseBuilder.RespostaErro("ConsultarNfseResposta", code), SoapAdapter.Erro(code));

    [TestMethod]
    public async Task LegacyPageOverflowAndDatesStayAtTheSoapEdge()
    {
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "examples/04-consultar-nfse.xml"))
            .Descendants(System.Xml.Linq.XName.Get("inputXML", SoapAdapter.Namespace)).Single().Value;
        var calls = 0;
        var soap = new SoapAdapter((query, _) =>
        {
            calls++;
            Assert.AreEqual(new Janela(-46, 5), query.Janela);
            Assert.AreEqual(new Periodo(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1), new DateTime(2026, 11, 1)), query.Periodo);
            return Task.FromResult(new ResultadoConsulta(new PaginaNfse([], false)));
        });
        var response = await soap.ConsultarAsync(source.Replace("<Pagina>1", "<Pagina>85899346").Replace("2026-10-31", "2026-11-01"), default);
        Assert.AreEqual(SoapAdapter.Erro("E040"), response);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task InnerDtdIsExplicitSecurityDivergence()
    {
        const string input = "<!DOCTYPE ConsultarNfseEnvio [<!ENTITY im '10000001'>]><ConsultarNfseEnvio><Prestador><InscricaoMunicipal>&im;</InscricaoMunicipal></Prestador></ConsultarNfseEnvio>";
        // Legacy does expand internal entities. The new adapter deliberately prohibits DTD.
        var legacy = LeitorLoteRps.Carregar(input);
        Assert.AreEqual("10000001", legacy.DocumentElement!.SelectSingleNode("Prestador/InscricaoMunicipal")!.InnerText);
        var soap = new SoapAdapter((_, _) => throw new AssertFailedException("DTD reached application."));
        Assert.AreEqual(SoapAdapter.Erro("E001"), await soap.ConsultarAsync(input, default));
    }
}
