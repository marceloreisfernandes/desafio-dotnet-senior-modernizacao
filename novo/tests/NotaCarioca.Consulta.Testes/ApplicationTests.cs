using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NotaCarioca.Consulta.Testes;

[TestClass]
public sealed class ApplicationTests
{
    internal static Nota Nota(long number = 7, SituacaoNota status = SituacaoNota.Normal, string document = "01234567000195") =>
        new(number, "V00000007", new DateTime(2026, 10, 2, 11, 19, 0), new DateOnly(2026, 10, 23),
            250.50m, 0m, 250.50m, .05m, 12.52m, false, "17.01", "A & B <x> \"q\" 'p'",
            new("01234567000195", "Prestador", "10000001"), new(document, "Tomador"), status,
            status == SituacaoNota.Cancelada ? new DateTime(2026, 10, 3) : null,
            status == SituacaoNota.Substituida ? 8 : null);

    internal static ConsultaSolicitada Request(string? document = null, Periodo? period = null) =>
        new("10000001", document, period ?? new(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), Janela.DaPagina(1));

    [DataTestMethod]
    [DataRow("01234567000195", true)]
    [DataRow("11100003000113", true)]
    [DataRow("52998224725", true)]
    [DataRow("01234567000194", false)]
    [DataRow("52998224724", false)]
    [DataRow("11111111111", false)]
    [DataRow("00000000000000", false)]
    [DataRow("01.234.567/0001-95", false)]
    [DataRow("xx01234567000195yy", false)]
    [DataRow("", false)]
    [DataRow("１２３４５６７８９０１", false)]
    public void CanonicalDocuments(string document, bool valid) => Assert.AreEqual(valid, DocumentoFiscal.Valido(document));

    [TestMethod]
    public async Task ValidationOrderAndNoInvoiceReadOnFailure()
    {
        var called = false;
        var application = new ConsultarNfse((_, _) => Task.FromResult<Prestador?>(null), (_, _) =>
        {
            called = true;
            throw new AssertFailedException("Invalid query reached invoice read.");
        });
        var invalid = Request("bad") with { Periodo = null };
        Assert.AreEqual(FalhaConsulta.PrestadorInexistente, (await application.ExecutarAsync(invalid, default)).Falha);
        application = new((_, _) => Task.FromResult<Prestador?>(new(1, true)), (_, _) => throw new AssertFailedException());
        Assert.AreEqual(FalhaConsulta.DocumentoInvalido, (await application.ExecutarAsync(invalid, default)).Falha);
        Assert.AreEqual(FalhaConsulta.PeriodoInvalido, (await application.ExecutarAsync(invalid with { DocumentoTomador = null }, default)).Falha);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task InactiveProviderDoesNotReadInvoices()
    {
        var application = new ConsultarNfse((_, _) => Task.FromResult<Prestador?>(new(1, false)),
            (_, _) => throw new AssertFailedException());
        Assert.AreEqual(FalhaConsulta.PrestadorInexistente, (await application.ExecutarAsync(Request(), default)).Falha);
    }

    [DataTestMethod]
    [DataRow(0, 0, false)]
    [DataRow(49, 49, false)]
    [DataRow(50, 50, false)]
    [DataRow(51, 50, true)]
    public async Task PaginationBoundariesRetainOracleAmountsAndStates(int rows, int shown, bool next)
    {
        using var cancellation = new CancellationTokenSource();
        var application = new ConsultarNfse((_, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            return Task.FromResult<Prestador?>(new(1, true));
        }, (query, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            Assert.AreEqual("01234567000195", query.DocumentoTomador);
            Assert.AreEqual(1L, query.PrestadorId);
            return Task.FromResult(PaginaNfse.DeLinhas(Enumerable.Range(1, rows)
                .Select(i => Nota(i, i % 2 == 0 ? SituacaoNota.Cancelada : SituacaoNota.Substituida)).ToArray()));
        });
        var result = await application.ExecutarAsync(Request("01234567000195"), cancellation.Token);
        Assert.IsNull(result.Falha);
        Assert.AreEqual(shown, result.Pagina!.Itens.Count);
        Assert.AreEqual(next, result.Pagina.ProximaPagina);
        if (shown > 0)
        {
            Assert.AreEqual(12.52m, result.Pagina.Itens[0].ValorIss);
            Assert.AreEqual(1L, result.Pagina.Itens[0].Numero);
            Assert.AreEqual(SituacaoNota.Substituida, result.Pagina.Itens[0].Situacao);
        }
    }

    [TestMethod]
    public async Task ApplicationRejectsInvalidTypedPeriod()
    {
        var application = new ConsultarNfse((_, _) => Task.FromResult<Prestador?>(new(1, true)),
            (_, _) => throw new AssertFailedException());
        foreach (var end in new[] { new DateTime(2026, 9, 30), new DateTime(2026, 11, 2) })
            Assert.AreEqual(FalhaConsulta.PeriodoInvalido,
                (await application.ExecutarAsync(Request(period: new(new DateTime(2026, 10, 1), end)), default)).Falha);
    }

    [TestMethod]
    public void ModernPageArithmeticDoesNotOverflow()
    {
        Assert.AreEqual(new Janela(49_999_950, 50_000_001), Janela.DaPagina(1_000_000));
        Assert.AreEqual(new Janela(107_374_182_300, 107_374_182_351), Janela.DaPagina(int.MaxValue));
    }
}
