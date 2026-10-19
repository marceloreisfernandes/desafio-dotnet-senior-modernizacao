using System.Globalization;

namespace NotaCarioca.Consulta;

public static class RestApi
{
    private static readonly HashSet<string> AllowedKeys = ["dataInicial", "dataFinal", "pagina", "documentoTomador"];

    public static async Task<IResult> ConsultarAsync(HttpContext context,
        Func<ConsultaSolicitada, CancellationToken, Task<ResultadoConsulta>> consultar)
    {
        var query = context.Request.Query;
        if (query.Any(pair => !AllowedKeys.Contains(pair.Key) || pair.Value.Count != 1))
            return Problem(400, "parametros-invalidos", "Parâmetros desconhecidos ou repetidos.");
        var im = context.Request.RouteValues["inscricaoMunicipal"]?.ToString() ?? "";
        if (im.Length != 8 || im.Any(c => c is < '0' or > '9'))
            return Problem(400, "inscricao-invalida", "Informe uma inscrição municipal com 8 dígitos.");
        if (!DateOnly.TryParseExact(query["dataInicial"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(query["dataFinal"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
            start > end || end.DayNumber - start.DayNumber > 30 || end == DateOnly.MaxValue)
            return Problem(400, "periodo-invalido", "Use datas yyyy-MM-dd, em ordem, com até 31 dias inclusivos e dataFinal anterior a 9999-12-31.");
        var page = 1;
        if (query.TryGetValue("pagina", out var rawPage) &&
            (!int.TryParse(rawPage, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page is < 1 or > 1_000_000))
            return Problem(400, "pagina-invalida", "A página deve ser um inteiro entre 1 e 1000000.");
        string? document = query.TryGetValue("documentoTomador", out var rawDocument) ? rawDocument.ToString() : null;
        if (document is not null && !DocumentoFiscal.Valido(document))
            return Problem(400, "documento-invalido", "Informe CPF/CNPJ válido, com 11/14 dígitos, sem pontuação.");
        var period = new Periodo(start.ToDateTime(TimeOnly.MinValue), end.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var result = await consultar(new(im, document, period, Janela.DaPagina(page)), context.RequestAborted);
        if (result.Falha is { } failure)
            return failure == FalhaConsulta.PrestadorInexistente
                ? Problem(404, "prestador-inexistente", "Prestador não encontrado ou inativo.")
                : Problem(400, "consulta-invalida", "Critérios de consulta inválidos.");
        return Results.Ok(new { itens = result.Pagina!.Itens, pagina = page,
            tamanhoPagina = Janela.Tamanho, proximaPagina = result.Pagina.ProximaPagina });
    }

    public static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, type: "urn:notacarioca:problema:" + code, title: title,
            extensions: new Dictionary<string, object?> { ["codigo"] = code });
}
