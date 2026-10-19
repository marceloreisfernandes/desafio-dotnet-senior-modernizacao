namespace NotaCarioca.Consulta;

// Application values: no transport, XML, legacy return codes or Oracle types.
public sealed record Periodo(DateTime Inicio, DateTime FimExclusivo, DateTime? CompetenciaMaxima = null);
public sealed record Janela(long Deslocamento, long Limite)
{
    public const int Tamanho = 50;
    public static Janela DaPagina(int pagina) => new(((long)pagina - 1) * Tamanho, (long)pagina * Tamanho + 1);
}
public sealed record ConsultaSolicitada(string InscricaoMunicipal, string? DocumentoTomador, Periodo? Periodo, Janela Janela);
public sealed record ConsultaValidada(long PrestadorId, string? DocumentoTomador, Periodo Periodo, Janela Janela);
public enum FalhaConsulta { PrestadorInexistente, DocumentoInvalido, PeriodoInvalido }
public enum SituacaoNota { Desconhecida, Normal, Cancelada, Substituida }
public sealed record Prestador(long Id, bool Ativo);
public sealed record Pessoa(string Documento, string RazaoSocial, string? InscricaoMunicipal = null);
public sealed record Nota(long Numero, string CodigoVerificacao, DateTime DataEmissao, DateOnly Competencia,
    decimal ValorServicos, decimal ValorDeducoes, decimal BaseCalculo, decimal Aliquota, decimal ValorIss,
    bool IssRetido, string ItemServico, string Discriminacao, Pessoa Prestador, Pessoa Tomador,
    SituacaoNota Situacao, DateTime? DataCancelamento, long? NumeroSubstituidora);
public sealed record PaginaNfse(IReadOnlyList<Nota> Itens, bool ProximaPagina)
{
    public static PaginaNfse DeLinhas(IReadOnlyList<Nota> linhas) =>
        new(linhas.Take(Janela.Tamanho).ToArray(), linhas.Count > Janela.Tamanho);
}
public sealed record ResultadoConsulta(PaginaNfse? Pagina = null, FalhaConsulta? Falha = null);
