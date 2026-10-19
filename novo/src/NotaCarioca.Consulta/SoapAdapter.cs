using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

namespace NotaCarioca.Consulta;

public sealed class SoapAdapter(Func<ConsultaSolicitada, CancellationToken, Task<ResultadoConsulta>> consultar)
{
    public const string Namespace = "http://notacarioca.rio.gov.br/";
    private const string EnvelopeStart = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>";
    private const string EnvelopeEnd = "</soap:Body></soap:Envelope>";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<string> ConsultarAsync(string input, CancellationToken ct)
    {
        XmlDocument doc = new() { XmlResolver = null };
        try
        {
            ValidarProfundidade(input);
            using var reader = XmlReader.Create(new StringReader(input), ReaderSettings());
            doc.Load(reader);
        }
        catch (XmlException) { return Erro("E001"); }
        var root = doc.DocumentElement;
        if (root is null || root.Name != "ConsultarNfseEnvio") return Erro("E001");

        var tomador = root.SelectSingleNode("Tomador");
        var recipient = tomador is null ? null : Texto(tomador, "CpfCnpj/Cnpj");
        if (string.IsNullOrEmpty(recipient) && tomador is not null) recipient = Texto(tomador, "CpfCnpj/Cpf");
        // The legacy ignores punctuation AND letters. Keep that permissiveness only here.
        recipient = string.IsNullOrEmpty(recipient) ? null : new string(recipient.Where(char.IsDigit).ToArray());
        int.TryParse(Texto(root, "Pagina"), out var page);
        if (page < 1) page = 1;
        // ERP compatibility includes the legacy's unchecked Int32 pagination arithmetic.
        var window = new Janela(unchecked((page - 1) * 50), unchecked(page * 50 + 1));
        var request = new ConsultaSolicitada(Texto(root, "Prestador/InscricaoMunicipal") ?? "", recipient,
            LerPeriodo(root), window);
        var result = await consultar(request, ct);
        if (result.Falha is { } failure)
            return Erro(failure switch
            {
                FalhaConsulta.PrestadorInexistente => "E010",
                FalhaConsulta.DocumentoInvalido => "E020", _ => "E030"
            });
        var response = result.Pagina!;
        if (response.Itens.Count == 0) return Erro("E040");
        return "<ConsultarNfseResposta><ListaNfse>" + string.Concat(response.Itens.Select(SerializarNota)) +
            "</ListaNfse><ProximaPagina>" + (response.ProximaPagina ? "true" : "false") +
            "</ProximaPagina></ConsultarNfseResposta>";
    }

    private static Periodo? LerPeriodo(XmlNode root)
    {
        if (!DateTime.TryParse(Texto(root, "PeriodoEmissao/DataInicial"), Inv, DateTimeStyles.None, out var start) ||
            !DateTime.TryParse(Texto(root, "PeriodoEmissao/DataFinal"), Inv, DateTimeStyles.None, out var end)) return null;
        start = start.Date;
        end = end.Date;
        return start > end || (end - start).TotalDays > 31 ? null : new(start, end, end);
    }

    private static string? Texto(XmlNode node, string path) => node.SelectSingleNode(path)?.InnerText.Trim();
    private static string Esc(string? value) => SecurityElement.Escape(value) ?? "";
    private static string Data(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);
    private static string Valor(decimal value) => value.ToString("0.00", Inv);

    public static string SerializarNota(Nota n)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<CompNfse><Nfse><InfNfse><Numero>").Append(n.Numero.ToString(Inv)).Append("</Numero>");
        sb.Append("<CodigoVerificacao>").Append(Esc(n.CodigoVerificacao)).Append("</CodigoVerificacao>");
        sb.Append("<DataEmissao>").Append(Data(n.DataEmissao)).Append("</DataEmissao>");
        sb.Append("<Competencia>").Append(n.Competencia.ToString("yyyy-MM-01", Inv)).Append("T00:00:00</Competencia>");
        sb.Append("<Servico><Valores><ValorServicos>").Append(Valor(n.ValorServicos)).Append("</ValorServicos>");
        sb.Append("<ValorDeducoes>").Append(Valor(n.ValorDeducoes)).Append("</ValorDeducoes>");
        sb.Append("<BaseCalculo>").Append(Valor(n.BaseCalculo)).Append("</BaseCalculo>");
        sb.Append("<Aliquota>").Append(n.Aliquota.ToString("0.####", Inv)).Append("</Aliquota>");
        sb.Append("<ValorIss>").Append(Valor(n.ValorIss)).Append("</ValorIss>");
        sb.Append("<IssRetido>").Append(n.IssRetido ? "1" : "2").Append("</IssRetido></Valores>");
        sb.Append("<ItemListaServico>").Append(Esc(n.ItemServico)).Append("</ItemListaServico>");
        sb.Append("<Discriminacao>").Append(Esc(n.Discriminacao)).Append("</Discriminacao></Servico>");
        sb.Append("<PrestadorServico><IdentificacaoPrestador><Cnpj>").Append(Esc(n.Prestador.Documento)).Append("</Cnpj>");
        sb.Append("<InscricaoMunicipal>").Append(Esc(n.Prestador.InscricaoMunicipal)).Append("</InscricaoMunicipal></IdentificacaoPrestador>");
        sb.Append("<RazaoSocial>").Append(Esc(n.Prestador.RazaoSocial)).Append("</RazaoSocial></PrestadorServico>");
        var tag = n.Tomador.Documento.Length > 11 ? "Cnpj" : "Cpf";
        var numericDocument = Convert.ToInt64(n.Tomador.Documento, Inv).ToString(Inv);
        sb.Append("<TomadorServico><IdentificacaoTomador><CpfCnpj><").Append(tag).Append('>')
            .Append(numericDocument).Append("</").Append(tag).Append("></CpfCnpj></IdentificacaoTomador>");
        sb.Append("<RazaoSocial>").Append(Esc(n.Tomador.RazaoSocial)).Append("</RazaoSocial></TomadorServico></InfNfse>");
        if (n.Situacao == SituacaoNota.Cancelada && n.DataCancelamento is { } cancelled)
            sb.Append("<NfseCancelamento><Confirmacao><DataHora>").Append(Data(cancelled))
                .Append("</DataHora></Confirmacao></NfseCancelamento>");
        if (n.Situacao == SituacaoNota.Substituida && n.NumeroSubstituidora is { } replacement)
            sb.Append("<NfseSubstituicao><NfseSubstituidora>").Append(replacement.ToString(Inv))
                .Append("</NfseSubstituidora></NfseSubstituicao>");
        return sb.Append("</Nfse></CompNfse>").ToString();
    }

    public static string Erro(string code)
    {
        var (message, correction) = code switch
        {
            "E001" => ("XML mal formado ou inválido.", "Verifique a estrutura do XML enviado."),
            "E010" => ("Prestador não encontrado ou inativo.", "Verifique a Inscrição Municipal informada."),
            "E020" => ("CPF/CNPJ inválido.", "Informe um CPF ou CNPJ válido."),
            "E030" => ("Período inválido.", "A data inicial deve ser menor ou igual à data final e o período não pode exceder 31 dias."),
            "E040" => ("Nenhuma NFS-e encontrada.", "Altere os critérios da consulta."),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        return "<ConsultarNfseResposta><ListaMensagemRetorno><MensagemRetorno><Codigo>" + code +
            "</Codigo><Mensagem>" + Esc(message) + "</Mensagem><Correcao>" + Esc(correction) +
            "</Correcao></MensagemRetorno></ListaMensagemRetorno></ConsultarNfseResposta>";
    }

    public static string Envelope(string output) => EnvelopeStart +
        "<ConsultarNfseResponse xmlns=\"" + Namespace + "\"><outputXML>" + Esc(output) +
        "</outputXML></ConsultarNfseResponse>" + EnvelopeEnd;

    public static string Fault(string code, string message) => EnvelopeStart + "<soap:Fault><faultcode>" + code +
        "</faultcode><faultstring>" + Esc(message) + "</faultstring></soap:Fault>" + EnvelopeEnd;

    // XmlNode.InnerText descends recursively. Bound depth before constructing/using the DOM.
    // Depth is zero-based: at most 64 nested elements, including the root.
    public const int MaxElementDepth = 64;
    private static void ValidarProfundidade(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings());
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.Depth >= MaxElementDepth)
                throw new XmlException("Profundidade XML excedida.");
    }

    public static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576
    };

    public static string LerInput(string envelope)
    {
        ValidarProfundidade(envelope);
        using var reader = XmlReader.Create(new StringReader(envelope), ReaderSettings());
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "inputXML")
                return reader.ReadElementContentAsString();
        return "";
    }
}
