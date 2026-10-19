namespace NotaCarioca.Consulta;

// Two functions keep the application independently testable without a repository hierarchy.
// In production both reads use the same Oracle connection, with statement-level consistency.
public sealed class ConsultarNfse(
    Func<string, CancellationToken, Task<Prestador?>> buscarPrestador,
    Func<ConsultaValidada, CancellationToken, Task<PaginaNfse>> buscarNotas)
{
    public async Task<ResultadoConsulta> ExecutarAsync(ConsultaSolicitada consulta, CancellationToken ct)
    {
        var prestador = string.IsNullOrEmpty(consulta.InscricaoMunicipal) ? null :
            await buscarPrestador(consulta.InscricaoMunicipal, ct);
        if (prestador is null || !prestador.Ativo) return new(Falha: FalhaConsulta.PrestadorInexistente);
        if (consulta.DocumentoTomador is not null && !DocumentoFiscal.Valido(consulta.DocumentoTomador))
            return new(Falha: FalhaConsulta.DocumentoInvalido);
        if (consulta.Periodo is null || consulta.Periodo.FimExclusivo < consulta.Periodo.Inicio ||
            (consulta.Periodo.FimExclusivo - consulta.Periodo.Inicio).TotalDays > 31)
            return new(Falha: FalhaConsulta.PeriodoInvalido);

        var linhas = await buscarNotas(new(prestador.Id, consulta.DocumentoTomador, consulta.Periodo, consulta.Janela), ct);
        return new(linhas);
    }
}
