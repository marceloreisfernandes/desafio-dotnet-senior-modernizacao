using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Fachada;

namespace NotaCarioca.Fachada.Testes;

[TestClass]
public sealed class RoutingTests
{
    private static string Envelope(string provider = "10000001", string extra = "") =>
        "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body>" +
        "<ConsultarNfseRequest xmlns='" + SoapRouting.Namespace + "'><inputXML>" +
        SecurityElement.Escape("<ConsultarNfseEnvio><Prestador><InscricaoMunicipal>" + provider +
        "</InscricaoMunicipal></Prestador><PeriodoEmissao><DataInicial>2026-10-01</DataInicial>" +
        "<DataFinal>2026-10-31</DataFinal></PeriodoEmissao>" + extra + "</ConsultarNfseEnvio>") +
        "</inputXML></ConsultarNfseRequest></soap:Body></soap:Envelope>";

    [DataTestMethod]
    [DataRow("legado", "sombra", "novo")]
    [DataRow("LEGACY", "SHADOW", "NEW")]
    public async Task DefaultLiveModesAllowlistPercentagesAndKillSwitch(string legacy, string shadow, string modern)
    {
        await using var server = await RuntimeServer.Start();
        await server.Expect("legacy"); // Missing file.
        server.Configure(modern, 100, providers: ["10000002"]);
        await server.Expect("legacy");
        server.Configure(modern, 100, providers: ["10000001"]);
        await server.Expect("modern");
        server.Configure(modern, 0);
        await server.Expect("legacy");
        server.Configure(modern, 100);
        await server.Expect("modern");
        server.Configure(legacy, 100);
        await server.Expect("legacy");
        server.Configure(modern, 50);
        // Pin both sides of a stable, documented SHA-256 cohort; never use randomized GetHashCode.
        const string low = "10000001"; // Independently calculated SHA-256 bucket 0.
        const string high = "10000002"; // Independently calculated SHA-256 bucket 76.
        for (var i = 0; i < 3; i++)
        {
            await server.Expect("modern", Envelope(low));
            await server.Expect("legacy", Envelope(high));
        }
        Assert.AreEqual(0, RoutingConfiguration.Bucket("10000001"));
        Assert.AreEqual(76, RoutingConfiguration.Bucket("10000002"));
        server.Configure(shadow, 100);
        await server.Expect("legacy");
        await server.WaitLog("unexpected-divergence");
        server.Configure(modern, 100);
        await server.Expect("modern");
        var watch = Stopwatch.StartNew();
        server.Configure(modern, 100, kill: true);
        await server.Expect("legacy");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
        Console.WriteLine($"Live LEGACY→NEW→LEGACY, allowlist, 0/50/100 and kill switch: {watch.ElapsedMilliseconds}ms; same process.");
    }

    [DataTestMethod]
    [DataRow("{broken")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"KillSwitch\":false,\"KillSwitch\":true,\"Operations\":{}}")]
    [DataRow("{\"KillSwitch\":false,\"Operations\":{\"CancelarNfse\":{\"Mode\":\"NEW\",\"Percentage\":100,\"Providers\":[]}}}")]
    [DataRow("{\"KillSwitch\":false,\"Operations\":{\"ConsultarNfse\":{\"Mode\":\"BOGUS\",\"Percentage\":100,\"Providers\":[]}}}")]
    [DataRow("{\"KillSwitch\":false,\"Operations\":{\"ConsultarNfse\":{\"Mode\":\"LEGACY, NEW\",\"Percentage\":100,\"Providers\":[]}}}")]
    [DataRow("{\"KillSwitch\":false,\"Operations\":{\"ConsultarNfse\":{\"Mode\":\"NEW\",\"Percentage\":101,\"Providers\":[]}}}")]
    [DataRow("{\"KillSwitch\":false,\"Operations\":{\"ConsultarNfse\":{\"Mode\":\"NEW\",\"Percentage\":100,\"Providers\":null}}}")]
    public async Task InvalidConfigurationRevokesPreviousNew(string json)
    {
        await using var server = await RuntimeServer.Start();
        server.Configure("novo", 100);
        await server.Expect("modern");
        File.WriteAllText(server.FilePath, json);
        await server.Expect("legacy");
    }

    [TestMethod]
    public async Task DeletedAndOversizedConfigurationFailLegacy()
    {
        await using var server = await RuntimeServer.Start();
        server.Configure("novo", 100);
        await server.Expect("modern");
        File.Delete(server.FilePath);
        await server.Expect("legacy");
        File.WriteAllText(server.FilePath, new string(' ', 16385));
        await server.Expect("legacy");
    }

    [DataTestMethod]
    [DataRow("novo")]
    [DataRow("NEW")]
    [DataRow("sombra")]
    [DataRow("SHADOW")]
    public async Task AllOtherActionsRemainSingleAttemptLegacy(string mode)
    {
        await using var server = await RuntimeServer.Start();
        server.Configure(mode, 100);
        foreach (var operation in new[] { "CancelarNfse", "RecepcionarLoteRps", "GerarNfse", "SubstituirNfse", "EnviarLoteRps",
            "ConsultarSituacaoLoteRps", "ConsultarLoteRps", "ConsultarNfseServicoTomado", "Unknown", "ConsultarNfse/CancelarNfse" })
        {
            using var response = await server.Send(Envelope(), SoapRouting.Namespace + operation);
            Assert.AreEqual("legacy", await response.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(10, server.LegacyCalls);
        Assert.AreEqual(0, server.ModernCalls);
    }

    [TestMethod]
    public async Task NewModeKeepsEntirePublicWsdlOnLegacy()
    {
        var xml = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "NfseWS.wsdl"));
        await using var server = await RuntimeServer.Start();
        server.LegacyHandler = c => c.Response.WriteAsync(xml);
        server.Configure("NEW", 100);
        using var response = await server.Client.GetAsync("/NfseWS.asmx?wsdl");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var expected = XDocument.Parse(xml);
        XNamespace soap = "http://schemas.xmlsoap.org/wsdl/soap/";
        foreach (var address in expected.Descendants(soap + "address"))
            address.SetAttributeValue("location", "http://localhost:8080/NfseWS.asmx");
        Assert.IsTrue(XNode.DeepEquals(expected, XDocument.Parse(await response.Content.ReadAsStringAsync())));
        Assert.AreEqual(1, server.LegacyCalls);
        Assert.AreEqual(0, server.ModernCalls);
    }

    [TestMethod]
    public async Task MalformedAmbiguousAndUninspectableRequestsStayOpaque()
    {
        await using var server = await RuntimeServer.Start();
        server.Configure("NEW", 100);
        foreach (var xml in new[] { "<broken", Envelope().Replace("ConsultarNfseRequest", "CancelarNfseRequest"),
            Envelope().Replace("</soap:Body>", "<other/></soap:Body>"),
            Envelope().Replace("<inputXML>", "<other/><inputXML>"),
            Envelope().Replace("10000001", "bad"),
            Envelope().Replace("&lt;/Prestador&gt;", "&lt;InscricaoMunicipal&gt;10000002&lt;/InscricaoMunicipal&gt;&lt;/Prestador&gt;"),
            Envelope(extra: "<!DOCTYPE unsafe>"), new string('x', SoapRouting.MaxRequestBytes + 1) })
            await server.Expect("legacy", xml);
        using var unknown = await server.Send(Envelope(), "http://untrusted/ConsultarNfse");
        Assert.AreEqual("legacy", await unknown.Content.ReadAsStringAsync());
        using var chunked = await server.Send(Envelope(), chunked: true);
        Assert.AreEqual("legacy", await chunked.Content.ReadAsStringAsync());
        using var queried = await server.Send(Envelope(), path: "/NfseWS.asmx?x=1");
        Assert.AreEqual("legacy", await queried.Content.ReadAsStringAsync());
        Assert.AreEqual(0, server.ModernCalls);
    }

    [TestMethod]
    public async Task ShadowReturnsExactLegacyAndReportsParityAndUnexpectedDivergence()
    {
        await using var server = await RuntimeServer.Start(modernHandler: c => c.Response.WriteAsync("legacy"));
        server.Configure("SHADOW", 100);
        await server.Expect("legacy");
        await server.WaitLog("parity");
        server.ModernHandler = c => c.Response.WriteAsync("different");
        await server.Expect("legacy");
        await server.WaitLog("unexpected-divergence");
        Assert.AreEqual(2, server.LegacyCalls);
        Assert.AreEqual(2, server.ModernCalls);
        Assert.IsFalse(server.Logs.Any(l => l.Contains("10000001") || l.Contains("ConsultarNfseEnvio")));
    }

    [TestMethod]
    public async Task ShadowPreservesLegacyStatusHeadersAndBytesWithoutCopyingCredentials()
    {
        byte[] reply = [0, 255, 13, 10, 65];
        var body = Encoding.UTF8.GetBytes(Envelope());
        await using var server = await RuntimeServer.Start(modernHandler: async c =>
        {
            Assert.IsFalse(c.Request.Headers.ContainsKey("Authorization"));
            Assert.IsFalse(c.Request.Headers.ContainsKey("Cookie"));
            Assert.IsFalse(c.Request.Headers.ContainsKey("X-Caller"));
            using var received = new MemoryStream();
            await c.Request.Body.CopyToAsync(received);
            CollectionAssert.AreEqual(body, received.ToArray());
            c.Response.StatusCode = 202;
            await c.Response.Body.WriteAsync(reply);
        });
        server.LegacyHandler = async c =>
        {
            Assert.AreEqual("Bearer synthetic", c.Request.Headers.Authorization.ToString());
            c.Response.StatusCode = 202;
            c.Response.Headers.Append("Set-Cookie", "synthetic=value");
            c.Response.Headers["X-Legacy"] = "unchanged";
            await c.Response.Body.WriteAsync(reply);
        };
        server.Configure("SHADOW", 100);
        using var request = Request(Envelope());
        request.Headers.Authorization = new("Bearer", "synthetic");
        request.Headers.Add("Cookie", "synthetic=value");
        request.Headers.Add("X-Caller", "synthetic");
        using var response = await server.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("unchanged", response.Headers.GetValues("X-Legacy").Single());
        Assert.AreEqual("synthetic=value", response.Headers.GetValues("Set-Cookie").Single());
        CollectionAssert.AreEqual(reply, await response.Content.ReadAsByteArrayAsync());
        await server.WaitLog("parity");
    }

    [TestMethod]
    public async Task NewDeadlineIncludesUploadInspectionAndNeverSendsAfterItExpires()
    {
        await using var server = await RuntimeServer.Start(modernTimeout: 1);
        server.Configure("NEW", 100);
        using var content = new GatedContent(Encoding.UTF8.GetBytes(Envelope()));
        using var request = Request(Envelope());
        request.Content = content;
        var pending = server.Client.SendAsync(request);
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(1200);
        content.Continue.TrySetResult();
        using var response = await pending;
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual(0, server.ModernCalls);
        Assert.AreEqual(0, server.LegacyCalls);
    }

    [TestMethod]
    public async Task ShadowTimeoutSaturationAndFailureDoNotDelayOrChangeClients()
    {
        var active = 0;
        var peak = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RuntimeServer.Start(modernHandler: async context =>
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            started.TrySetResult();
            try { await Task.Delay(10000, context.RequestAborted); }
            catch (OperationCanceledException) { }
            finally { Interlocked.Decrement(ref active); }
        }, shadowTimeout: 1000, concurrency: 1);
        server.Configure("SHADOW", 100);
        await server.Expect("legacy");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => server.Expect("legacy")));
        watch.Stop();
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(800), $"Legacy responses took {watch.ElapsedMilliseconds}ms");
        await server.WaitLog("capacity-skipped");
        await server.WaitLog("timeout-or-stop");
        Assert.AreEqual(1, peak);
        Assert.AreEqual(1, server.ModernCalls);
        server.ModernHandler = c => { c.Abort(); return Task.CompletedTask; };
        await server.Expect("legacy");
        await server.WaitLog("failure");
        Console.WriteLine($"12 legacy responses under slow shadow: {watch.ElapsedMilliseconds}ms (timeout 1000ms); peak modern={peak}.");
    }

    [TestMethod]
    public async Task ShadowResponseSizeIsBoundedAndLegacyBytesSurvive()
    {
        await using var server = await RuntimeServer.Start(modernHandler: c => c.Response.WriteAsync(new string('x', ResponseCapture.MaxBytes + 1)));
        server.Configure("SHADOW", 100);
        await server.Expect("legacy");
        await server.WaitLog("response-limit");
    }

    [TestMethod]
    public async Task NewAmbiguousFailureAndTimeoutNeverReplayToLegacy()
    {
        await using var server = await RuntimeServer.Start(modernHandler: c => { c.Abort(); return Task.CompletedTask; }, modernTimeout: 1);
        server.Configure("NEW", 100);
        using (var response = await server.Send(Envelope())) Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.AreEqual(0, server.LegacyCalls);
        Assert.AreEqual(1, server.ModernCalls);
        server.ModernHandler = async c =>
        {
            try { await Task.Delay(10000, c.RequestAborted); } catch (OperationCanceledException) { }
        };
        using (var response = await server.Send(Envelope())) Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual(0, server.LegacyCalls);
        Assert.AreEqual(2, server.ModernCalls);
        server.Configure("NEW", 100, kill: true);
        await server.Expect("legacy");
    }

    [TestMethod]
    public void StrictCharacterDifferenceHasNarrowClassification()
    {
        Assert.IsTrue(SoapRouting.Inspect(Encoding.UTF8.GetBytes(Envelope(extra: "<Extra>&#1;</Extra>")))!.StrictCharacterDivergence);
        var legacy = Capture(Reply("<ConsultarNfseResposta><ListaNfse><CompNfse/></ListaNfse></ConsultarNfseResposta>"));
        var modern = Capture(Reply("<ConsultarNfseResposta><ListaMensagemRetorno><MensagemRetorno><Codigo>E001</Codigo>" +
            "<Mensagem>XML mal formado ou inválido.</Mensagem><Correcao>Verifique a estrutura do XML enviado.</Correcao>" +
            "</MensagemRetorno></ListaMensagemRetorno></ConsultarNfseResposta>"));
        Assert.AreEqual("expected-strict-xml", ShadowConsultation.Compare(new("10000001", true), legacy, modern));
        Assert.AreEqual("unexpected-divergence", ShadowConsultation.Compare(new("10000001", false), legacy, modern));
    }

    private static string Reply(string inner) => "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>" +
        "<soap:Body><ConsultarNfseResponse xmlns='" + SoapRouting.Namespace + "'><outputXML>" + SecurityElement.Escape(inner) +
        "</outputXML></ConsultarNfseResponse></soap:Body></soap:Envelope>";
    private static ResponseCapture Capture(string body)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        var capture = new ResponseCapture();
        capture.Start(response);
        capture.Append(Encoding.UTF8.GetBytes(body));
        capture.Complete = true;
        return capture;
    }

    [TestMethod]
    [TestCategory("Oracle")]
    public async Task RealReadOnlyOracleFixturesThroughRuntimeStrangler()
    {
        var legacy = Environment.GetEnvironmentVariable("NFSE_LEGACY_BASE_URL");
        var modern = Environment.GetEnvironmentVariable("NFSE_MODERN_BASE_URL");
        if (string.IsNullOrEmpty(legacy) || string.IsNullOrEmpty(modern)) Assert.Inconclusive("Set NFSE_LEGACY_BASE_URL and NFSE_MODERN_BASE_URL for read-only Oracle SOAP tests.");
        await using var server = await RuntimeServer.Start(externalLegacy: legacy, externalModern: modern);
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "04-consultar-nfse.xml"));
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = Request(fixture);
        request.RequestUri = new Uri(legacy + "/NfseWS.asmx");
        using var original = await direct.SendAsync(request);
        var expected = await original.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(HttpStatusCode.OK, original.StatusCode);
        using (var defaultResponse = await server.Send(fixture))
            CollectionAssert.AreEqual(expected, await defaultResponse.Content.ReadAsByteArrayAsync());
        foreach (var mode in new[] { "LEGACY", "NEW", "SHADOW" })
        {
            server.Configure(mode, 100);
            using var response = await server.Send(fixture);
            Assert.AreEqual(original.StatusCode, response.StatusCode);
            Assert.AreEqual(original.Content.Headers.ContentType!.ToString(), response.Content.Headers.ContentType!.ToString());
            CollectionAssert.AreEqual(expected, await response.Content.ReadAsByteArrayAsync(), mode);
        }
        await server.WaitLog("parity");
        // Existing review fixture: legacy accepts an invalid XML character reference; modern returns E001.
        var divergent = Envelope(extra: "<Ignorado>&#1;</Ignorado>");
        using var directDivergent = Request(divergent);
        directDivergent.RequestUri = new Uri(legacy + "/NfseWS.asmx");
        using var legacyDivergent = await direct.SendAsync(directDivergent);
        using var shadowDivergent = await server.Send(divergent);
        CollectionAssert.AreEqual(await legacyDivergent.Content.ReadAsByteArrayAsync(), await shadowDivergent.Content.ReadAsByteArrayAsync());
        await server.WaitLog("expected-strict-xml");
        server.Configure("NEW", 100, kill: true);
        using var killed = await server.Send(fixture);
        CollectionAssert.AreEqual(expected, await killed.Content.ReadAsByteArrayAsync());
        Console.WriteLine("Oracle: fixture LEGACY/NEW/SHADOW byte parity; known strict-character divergence observed; shadow unchanged; live kill switch passed. No fiscal writes.");
    }

    private static HttpRequestMessage Request(string xml, string action = SoapRouting.Action, string path = "/NfseWS.asmx", bool chunked = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", '"' + action + '"');
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return request;
    }

    private sealed class RuntimeServer : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "nfse-routing-" + Guid.NewGuid().ToString("N"));
        private WebApplication legacy = null!, modern = null!, facade = null!;
        public string FilePath => Path.Combine(directory, "routing.json");
        public HttpClient Client { get; private set; } = null!;
        private int legacyCalls, modernCalls;
        public int LegacyCalls => legacyCalls;
        public int ModernCalls => modernCalls;
        public ConcurrentQueue<string> Logs { get; } = new();
        public RequestDelegate ModernHandler { get; set; } = c => c.Response.WriteAsync("modern");
        public RequestDelegate LegacyHandler { get; set; } = c => c.Response.WriteAsync("legacy");

        public static async Task<RuntimeServer> Start(RequestDelegate? modernHandler = null, int shadowTimeout = 2000,
            int concurrency = 2, int modernTimeout = 30, string? externalLegacy = null, string? externalModern = null)
        {
            var server = new RuntimeServer();
            Directory.CreateDirectory(server.directory);
            if (modernHandler is not null) server.ModernHandler = modernHandler;
            server.legacy = await Upstream(async c =>
            {
                Interlocked.Increment(ref server.legacyCalls);
                c.Response.ContentType = "text/xml; charset=utf-8";
                await server.LegacyHandler(c);
            });
            server.modern = await Upstream(async c =>
            {
                Interlocked.Increment(ref server.modernCalls);
                c.Response.ContentType = "text/xml; charset=utf-8";
                await server.ModernHandler(c);
            });
            server.facade = FacadeApplication.Build([
                "--urls=http://127.0.0.1:0", "--Legacy:BaseUrl=" + (externalLegacy ?? Address(server.legacy)),
                "--Modern:BaseUrl=" + (externalModern ?? Address(server.modern)), "--Routing:File=" + server.FilePath,
                "--Modern:ShadowTimeoutMilliseconds=" + shadowTimeout, "--Modern:ShadowConcurrency=" + concurrency,
                "--Modern:TimeoutSeconds=" + modernTimeout, "--Logging:LogLevel:Default=Information"
            ]);
            server.facade.Services.GetRequiredService<ILoggerFactory>().AddProvider(new LogProvider(server.Logs));
            await server.facade.StartAsync();
            server.Client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(Address(server.facade)) };
            return server;
        }

        public void Configure(string mode, int percentage, bool kill = false, string[]? providers = null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                KillSwitch = kill, Operations = new Dictionary<string, object>
                { ["ConsultarNfse"] = new { Mode = mode, Percentage = percentage, Providers = providers ?? [] } }
            });
            var temporary = Path.Combine(directory, "next.json");
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        public Task<HttpResponseMessage> Send(string xml, string action = SoapRouting.Action, string path = "/NfseWS.asmx", bool chunked = false) =>
            Client.SendAsync(Request(xml, action, path, chunked));
        public async Task Expect(string expected, string? xml = null)
        {
            using var response = await Send(xml ?? Envelope());
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(expected, await response.Content.ReadAsStringAsync());
        }
        public async Task WaitLog(string category)
        {
            var limit = Stopwatch.StartNew();
            while (!Logs.Any(l => l == "Sombra ConsultarNfse: " + category))
            {
                if (limit.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("Missing shadow outcome: " + category);
                await Task.Delay(10);
            }
        }
        private static async Task<WebApplication> Upstream(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(handler);
            await app.StartAsync();
            return app;
        }
        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await facade.DisposeAsync();
            await modern.DisposeAsync();
            await legacy.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class GatedContent : HttpContent
    {
        private readonly byte[] bytes;
        public GatedContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await stream.FlushAsync();
            Started.TrySetResult();
            await Continue.Task;
            await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2));
        }
    }

    private sealed class LogProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(logs);
        public void Dispose() { }
        private sealed class CapturedLogger(ConcurrentQueue<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Enqueue(formatter(state, exception));
        }
    }
}
