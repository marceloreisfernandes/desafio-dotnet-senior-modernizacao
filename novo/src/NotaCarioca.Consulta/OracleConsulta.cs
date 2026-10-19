using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Consulta;

public sealed class OracleConsulta(string connectionString)
{
    public const int TimeoutSeconds = 30;

    public async Task<ResultadoConsulta> ExecutarAsync(ConsultaSolicitada consulta, CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        // Fixed SELECTs only; statement-level consistency, as in the legacy service.
        var application = new ConsultarNfse(
            (im, token) => BuscarPrestador(connection, im, token),
            (query, token) => BuscarNotas(connection, query, token));
        return await application.ExecutarAsync(consulta, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = Command(connection, "SELECT 1 FROM DUAL");
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }

    private static OracleCommand Command(OracleConnection connection, string sql) => new(sql, connection)
    {
        BindByName = true,
        CommandTimeout = TimeoutSeconds
    };

    private static async Task<Prestador?> BuscarPrestador(OracleConnection connection, string im, CancellationToken ct)
    {
        using var command = Command(connection,
            "SELECT ID, ATIVO FROM CONTRIBUINTES WHERE INSCRICAO_MUNICIPAL = :im");
        command.Parameters.Add("im", OracleDbType.Varchar2, im, ParameterDirection.Input);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetInt64(0), reader.GetString(1) == "S") : null;
    }

    private static async Task<PaginaNfse> BuscarNotas(OracleConnection connection, ConsultaValidada query, CancellationToken ct)
    {
        // Only fixed SQL fragments are composed; every value is bound with its Oracle type.
        var recipientFilter = query.DocumentoTomador is null ? "" : " AND n.TOMADOR_DOCUMENTO = :doc";
        var competenceFilter = query.Periodo.CompetenciaMaxima is null ? "" : " AND n.COMPETENCIA <= :competencia";
        using var command = Command(connection, $"""
            SELECT * FROM (
              SELECT a.*, ROWNUM RN FROM (
                SELECT n.NUMERO, n.CODIGO_VERIFICACAO, n.DT_EMISSAO, n.COMPETENCIA,
                       n.VALOR_SERVICOS, n.VALOR_DEDUCOES, n.BASE_CALCULO, n.ALIQUOTA, n.VALOR_ISS,
                       n.ISS_RETIDO, n.ITEM_SERVICO, n.DISCRIMINACAO,
                       p.DOCUMENTO, p.RAZAO_SOCIAL, p.INSCRICAO_MUNICIPAL,
                       n.TOMADOR_DOCUMENTO, n.TOMADOR_RAZAO, n.SITUACAO, n.DT_CANCELAMENTO,
                       (SELECT s.NUMERO FROM NFSE s WHERE s.ID = n.NFSE_SUBSTITUIDORA_ID) NUM_SUBSTITUIDORA
                  FROM NFSE n JOIN CONTRIBUINTES p ON p.ID = n.PRESTADOR_ID
                 WHERE n.PRESTADOR_ID = :prest
                   AND n.DT_EMISSAO >= :ini AND n.DT_EMISSAO < :fim{competenceFilter}{recipientFilter}
                 ORDER BY n.DT_EMISSAO, n.ID
              ) a WHERE ROWNUM <= :limite
            ) WHERE RN > :deslocamento ORDER BY RN
            """);
        command.Parameters.Add("prest", OracleDbType.Int64, query.PrestadorId, ParameterDirection.Input);
        command.Parameters.Add("ini", OracleDbType.Date, query.Periodo.Inicio, ParameterDirection.Input);
        command.Parameters.Add("fim", OracleDbType.Date, query.Periodo.FimExclusivo, ParameterDirection.Input);
        if (query.Periodo.CompetenciaMaxima is { } maximum)
            command.Parameters.Add("competencia", OracleDbType.Date, maximum, ParameterDirection.Input);
        if (query.DocumentoTomador is { } document)
            command.Parameters.Add("doc", OracleDbType.Varchar2, document, ParameterDirection.Input);
        command.Parameters.Add("limite", OracleDbType.Int64, query.Janela.Limite, ParameterDirection.Input);
        command.Parameters.Add("deslocamento", OracleDbType.Int64, query.Janela.Deslocamento, ParameterDirection.Input);

        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        return await LerPaginaAsync(() => reader.ReadAsync(ct), () =>
        {
            // Read in ordinal order for SequentialAccess; retain monetary amounts from Oracle.
            var number = reader.GetInt64(0);
            var code = Text(reader, 1);
            var emission = reader.GetDateTime(2);
            var competence = DateOnly.FromDateTime(reader.GetDateTime(3));
            var services = reader.GetDecimal(4);
            var deductions = reader.GetDecimal(5);
            var basis = reader.GetDecimal(6);
            var rate = reader.GetDecimal(7);
            var tax = reader.GetDecimal(8);
            var withheld = Text(reader, 9) == "S";
            var item = Text(reader, 10);
            var description = Text(reader, 11);
            var providerDocument = Text(reader, 12);
            var providerName = Text(reader, 13);
            var im = Text(reader, 14);
            var recipientDocument = Text(reader, 15);
            var recipientName = Text(reader, 16);
            var status = Text(reader, 17) switch
            {
                "N" => SituacaoNota.Normal, "C" => SituacaoNota.Cancelada,
                "S" => SituacaoNota.Substituida, _ => SituacaoNota.Desconhecida
            };
            DateTime? cancelled = reader.IsDBNull(18) ? null : reader.GetDateTime(18);
            long? replacement = reader.IsDBNull(19) ? null : reader.GetInt64(19);
            return new Nota(number, code, emission, competence, services, deductions, basis, rate, tax, withheld,
                item, description, new(providerDocument, providerName, im), new(recipientDocument, recipientName),
                status, cancelled, replacement);
        });
    }

    internal static async Task<PaginaNfse> LerPaginaAsync(Func<Task<bool>> read, Func<Nota> decode)
    {
        var result = new List<Nota>(Janela.Tamanho);
        while (result.Count < Janela.Tamanho && await read()) result.Add(decode());
        // Advance once only to detect another page; never decode the extra row.
        return new(result, result.Count == Janela.Tamanho && await read());
    }

    private static string Text(OracleDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
}
