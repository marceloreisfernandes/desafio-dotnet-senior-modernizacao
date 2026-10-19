using System.Data;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Servicos;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
public sealed class LegacyCharacterizationTests
{
    [DataTestMethod]
    [DataRow("E001", "XML mal formado ou inválido.")]
    [DataRow("E010", "Prestador não encontrado ou inativo.")]
    [DataRow("E020", "CPF/CNPJ inválido.")]
    [DataRow("E030", "Período inválido.")]
    [DataRow("E040", "Nenhuma NFS-e encontrada.")]
    public void LegacyErrorContract(string code, string message)
    {
        var xml = XDocument.Parse(XmlNfseBuilder.RespostaErro("ConsultarNfseResposta", code));
        Assert.AreEqual("ConsultarNfseResposta", xml.Root!.Name.LocalName);
        Assert.AreEqual(code, xml.Descendants("Codigo").Single().Value);
        Assert.AreEqual(message, xml.Descendants("Mensagem").Single().Value);
        Assert.IsFalse(string.IsNullOrEmpty(xml.Descendants("Correcao").Single().Value));
    }

    [TestMethod]
    public void StoredExamplesContainUnpaddedRecipientAndOriginalIss()
    {
        XNamespace ns = "http://notacarioca.rio.gov.br/";
        var outer = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "examples/04-consultar-nfse.resposta.xml"));
        var inner = XDocument.Parse(outer.Descendants(ns + "outputXML").Single().Value);
        CollectionAssert.AreEqual(new[] { "23", "22", "21" }, inner.Descendants("Numero").Select(n => n.Value).ToArray());
        Assert.AreEqual("3703701000185", inner.Descendants("TomadorServico").First().Descendants("Cnpj").Single().Value);
        Assert.AreEqual("418.72", inner.Descendants("ValorIss").First().Value);
        Assert.AreEqual("false", inner.Root!.Element("ProximaPagina")!.Value);
    }

    [TestMethod]
    public void LegacySerializerGoldenRow()
    {
        var xml = XmlNfseBuilder.CompNfse(Row());
        Assert.AreEqual("<CompNfse><Nfse><InfNfse><Numero>7</Numero><CodigoVerificacao>V00000007</CodigoVerificacao>" +
            "<DataEmissao>2026-10-02T11:19:00</DataEmissao><Competencia>2026-10-01T00:00:00</Competencia>" +
            "<Servico><Valores><ValorServicos>250.50</ValorServicos><ValorDeducoes>0.00</ValorDeducoes>" +
            "<BaseCalculo>250.50</BaseCalculo><Aliquota>0.05</Aliquota><ValorIss>12.52</ValorIss><IssRetido>2</IssRetido>" +
            "</Valores><ItemListaServico>17.01</ItemListaServico><Discriminacao>A &amp; B &lt;x&gt; &quot;q&quot; &apos;p&apos;</Discriminacao>" +
            "</Servico><PrestadorServico><IdentificacaoPrestador><Cnpj>01234567000195</Cnpj>" +
            "<InscricaoMunicipal>10000001</InscricaoMunicipal></IdentificacaoPrestador><RazaoSocial>Prestador</RazaoSocial>" +
            "</PrestadorServico><TomadorServico><IdentificacaoTomador><CpfCnpj><Cnpj>1234567000195</Cnpj></CpfCnpj>" +
            "</IdentificacaoTomador><RazaoSocial>Tomador</RazaoSocial></TomadorServico></InfNfse></Nfse></CompNfse>", xml);
    }

    internal static DataRow Row(string status = "N", string document = "01234567000195")
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var column in new[] { "NUMERO", "CODIGO_VERIFICACAO", "DT_EMISSAO", "COMPETENCIA", "VALOR_SERVICOS",
            "VALOR_DEDUCOES", "BASE_CALCULO", "ALIQUOTA", "VALOR_ISS", "ISS_RETIDO", "ITEM_SERVICO", "DISCRIMINACAO",
            "PRESTADOR_DOC", "PRESTADOR_IM", "PRESTADOR_RAZAO", "TOMADOR_DOCUMENTO", "TOMADOR_RAZAO", "SITUACAO",
            "DT_CANCELAMENTO", "NUM_SUBSTITUIDORA" }) table.Columns.Add(column, typeof(object));
        return table.Rows.Add(7L, "V00000007", new DateTime(2026, 10, 2, 11, 19, 0), new DateTime(2026, 10, 23),
            250.50m, 0m, 250.50m, .05m, 12.52m, "N", "17.01", "A & B <x> \"q\" 'p'", "01234567000195", "10000001",
            "Prestador", document, "Tomador", status, status == "C" ? new DateTime(2026, 10, 3) : DBNull.Value,
            status == "S" ? 8L : DBNull.Value);
    }
}
