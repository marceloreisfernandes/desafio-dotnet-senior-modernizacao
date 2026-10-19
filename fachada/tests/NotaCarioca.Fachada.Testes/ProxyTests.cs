using System.Net;
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
public sealed class ProxyTests
{
    [DataTestMethod]
    [DataRow("04-consultar-nfse.xml", "ConsultarNfse")]
    [DataRow("05-consultar-nfse-servico-tomado.xml", "ConsultarNfseServicoTomado")]
    public async Task ReadOnlyExamplesPreserveExactBytesAndHeaders(string file, string operation)
    {
        var body = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, file));
        var reply = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><soap>á &amp; \r\n</soap>");
        await using var pair = await Servers.Start(async ctx =>
        {
            Assert.AreEqual("POST", ctx.Request.Method);
            Assert.AreEqual("?x=a%2Fb&x=2", ctx.Request.QueryString.Value);
            Assert.AreEqual($"\"http://notacarioca.rio.gov.br/{operation}\"", ctx.Request.Headers["SOAPAction"].ToString());
            Assert.AreEqual("text/xml; charset=utf-8", ctx.Request.ContentType);
            Assert.AreEqual("Bearer synthetic", ctx.Request.Headers.Authorization.ToString());
            Assert.AreEqual("session=synthetic", ctx.Request.Headers.Cookie.ToString());
            Assert.AreEqual("yes", ctx.Request.Headers["X-Custom"].ToString());
            Assert.IsFalse(ctx.Request.Headers.ContainsKey("X-Hop"));
            using var received = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(received);
            CollectionAssert.AreEqual(body, received.ToArray());
            ctx.Response.StatusCode = 202;
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            ctx.Response.Headers.Append("Set-Cookie", "a=1");
            ctx.Response.Headers.Append("Set-Cookie", "b=2");
            ctx.Response.Headers["Connection"] = "X-Upstream-Hop";
            ctx.Response.Headers["X-Upstream-Hop"] = "secret";
            await ctx.Response.Body.WriteAsync(reply);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx?x=a%2Fb&x=2");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=utf-8");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://notacarioca.rio.gov.br/{operation}\"");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic");
        request.Headers.TryAddWithoutValidation("Cookie", "session=synthetic");
        request.Headers.Add("X-Custom", "yes");
        request.Headers.Add("Connection", "X-Hop");
        request.Headers.Add("X-Hop", "secret");
        using var response = await pair.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("text/xml; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        CollectionAssert.AreEqual(reply, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(2, response.Headers.GetValues("Set-Cookie").Count());
        Assert.IsFalse(response.Headers.Contains("X-Upstream-Hop"));
        Assert.AreEqual(1, pair.Calls);
    }

    [DataTestMethod]
    [DataRow("GET", 200)]
    [DataRow("POST", 500)]
    [DataRow("PUT", 405)]
    [DataRow("DELETE", 400)]
    [DataRow("PATCH", 503)]
    public async Task OpaqueRequestsAndFaultsArePassedThrough(string method, int status)
    {
        byte[] body = [0, 255, 13, 10, 32, 65];
        await using var pair = await Servers.Start(async ctx =>
        {
            Assert.AreEqual(method, ctx.Request.Method);
            Assert.AreEqual("unknown-action", ctx.Request.Headers["SOAPAction"].ToString());
            using var bytes = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(bytes);
            CollectionAssert.AreEqual(body, bytes.ToArray());
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/soap+xml; charset=iso-8859-1";
            await ctx.Response.Body.WriteAsync(body);
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), "/NfseWS.asmx") { Content = new ByteArrayContent(body) };
        request.Headers.Add("SOAPAction", "unknown-action");
        using var response = await pair.Client.SendAsync(request);
        Assert.AreEqual(status, (int)response.StatusCode);
        CollectionAssert.AreEqual(body, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("application/soap+xml; charset=iso-8859-1", response.Content.Headers.ContentType!.ToString());
        Assert.AreEqual(1, pair.Calls);
    }

    [DataTestMethod]
    [DataRow("/NfseWS.asmx?wsdl")]
    [DataRow("/NfseWS.asmx/?wsdl")]
    public async Task WsdlChangesOnlyServiceAddressAndDropsOldValidators(string path)
    {
        const string xml = "<wsdl:definitions xmlns:wsdl='http://schemas.xmlsoap.org/wsdl/' xmlns:soap='http://schemas.xmlsoap.org/wsdl/soap/'><wsdl:service name='NfseWS'><wsdl:port name='soap'><soap:address location='http://private:8080/NfseWS.asmx'/></wsdl:port></wsdl:service></wsdl:definitions>";
        await using var pair = await Servers.Start(async ctx =>
        {
            Assert.IsTrue(ctx.Request.Query.ContainsKey("wsdl"));
            Assert.IsFalse(ctx.Request.Headers.ContainsKey("If-None-Match"));
            Assert.IsFalse(ctx.Request.Headers.ContainsKey("Accept-Encoding"));
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            ctx.Response.Headers.ETag = "\"private-version\"";
            await ctx.Response.WriteAsync(xml);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("If-None-Match", "\"private-version\"");
        request.Headers.Add("Accept-Encoding", "gzip");
        request.Headers.Host = "untrusted.example";
        using var response = await pair.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var actual = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var expected = XDocument.Parse(xml);
        XNamespace soap = "http://schemas.xmlsoap.org/wsdl/soap/";
        expected.Descendants(soap + "address").Single().SetAttributeValue("location", "https://public.example/NfseWS.asmx");
        Assert.IsTrue(XNode.DeepEquals(expected, actual));
        Assert.IsNull(response.Headers.ETag);
    }

    [DataTestMethod]
    [DataRow("/health", 200)]
    [DataRow("/health", 503)]
    [DataRow("/health/ready", 200)]
    [DataRow("/health/ready", 503)]
    public async Task ReadinessUsesLegacyHealth(string path, int status)
    {
        await using var pair = await Servers.Start(async ctx =>
        {
            Assert.AreEqual("/health", ctx.Request.Path.Value);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync(status == 200 ? "OK" : "INDISPONIVEL");
        });
        using var response = await pair.Client.GetAsync(path);
        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.AreEqual(status == 200 ? "OK" : "INDISPONIVEL", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task LivenessDoesNotCallLegacy()
    {
        await using var pair = await Servers.Start(_ => throw new AssertFailedException("Unexpected upstream request"));
        Assert.AreEqual("OK", await pair.Client.GetStringAsync("/health/live"));
        Assert.AreEqual(0, pair.Calls);
    }

    [TestMethod]
    public async Task RedirectIsReturnedWithoutFollowingOrRetryingPost()
    {
        await using var pair = await Servers.Start(ctx =>
        {
            ctx.Response.StatusCode = 307;
            ctx.Response.Headers.Location = "/must-not-follow";
            return Task.CompletedTask;
        });
        using var response = await pair.Client.PostAsync("/NfseWS.asmx", new StringContent("synthetic"));
        Assert.AreEqual(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.AreEqual("/must-not-follow", response.Headers.Location!.OriginalString);
        Assert.AreEqual(1, pair.Calls);
    }

    [TestMethod]
    public async Task TimeoutBeforeHeadersIs504AndPostIsNotRetried()
    {
        await using var pair = await Servers.Start(async ctx =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        }, timeoutSeconds: 1);
        using var response = await pair.Client.PostAsync("/NfseWS.asmx", new StringContent("synthetic"));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual(1, pair.Calls);
        Assert.AreEqual("Tempo limite do legado excedido.", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task BrokenUpstreamIs502WithoutLeakingDetails()
    {
        await using var pair = await Servers.Start(ctx => { ctx.Abort(); return Task.CompletedTask; });
        using var response = await pair.Client.PostAsync("/NfseWS.asmx", new StringContent("sensitive synthetic data"));
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.AreEqual("Falha de comunicação com o legado.", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, pair.Calls);
    }

    [TestMethod]
    public async Task TimeoutAfterHeadersAbortsPartialResponse()
    {
        await using var pair = await Servers.Start(async ctx =>
        {
            await ctx.Response.WriteAsync("<incomplete>");
            await ctx.Response.Body.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        }, timeoutSeconds: 1);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await pair.Client.GetStringAsync("/NfseWS.asmx"));
        Assert.AreEqual(1, pair.Calls);
    }

    [TestMethod]
    public async Task ClientDisconnectCancelsUpstream()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await Servers.Start(async ctx =>
        {
            entered.SetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        using var cancellation = new CancellationTokenSource();
        var pending = pair.Client.GetAsync("/NfseWS.asmx", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, pair.Calls);
    }

    [TestMethod]
    public async Task StalledWsdlBodyIsBoundedAndReturns504()
    {
        await using var pair = await Servers.Start(async ctx =>
        {
            await ctx.Response.WriteAsync("<definitions>");
            await ctx.Response.Body.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        }, timeoutSeconds: 1);
        using var response = await pair.Client.GetAsync("/NfseWS.asmx?wsdl");
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [TestMethod]
    public async Task InvalidWsdlReturns502()
    {
        await using var pair = await Servers.Start(ctx => ctx.Response.WriteAsync("<invalid"));
        using var response = await pair.Client.GetAsync("/NfseWS.asmx?wsdl");
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    public async Task UpstreamWsdlFailureIsNotParsedOrHidden()
    {
        await using var pair = await Servers.Start(async ctx =>
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("upstream unavailable");
        });
        using var response = await pair.Client.GetAsync("/NfseWS.asmx?wsdl");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("upstream unavailable", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CompressedSoapBytesAreNotDecompressed()
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("<soap>synthetic</soap>"));
            compressed = output.ToArray();
        }
        await using var pair = await Servers.Start(async ctx =>
        {
            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentType = "text/xml";
            ctx.Response.ContentLength = compressed.Length;
            await ctx.Response.Body.WriteAsync(compressed);
        });
        using var response = await pair.Client.PostAsync("/NfseWS.asmx", new StringContent("synthetic"));
        CollectionAssert.AreEqual(compressed, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("gzip", response.Content.Headers.ContentEncoding.Single());
    }

    [TestMethod]
    public void InvalidConfigurationFailsAtStartup()
    {
        Assert.ThrowsException<InvalidOperationException>(() => new ProxyOptions { TimeoutSeconds = 0 }.Validate());
        Assert.ThrowsException<InvalidOperationException>(() => new ProxyOptions { BaseUrl = "file:///etc/" }.Validate());
        Assert.ThrowsException<InvalidOperationException>(() => new ProxyOptions { PublicServiceUrl = "http://public/other" }.Validate());
    }

    private sealed class Servers : IAsyncDisposable
    {
        private WebApplication upstream = null!;
        private WebApplication facade = null!;
        private int calls;
        public int Calls => calls;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<Servers> Start(RequestDelegate handler, int timeoutSeconds = 15)
        {
            var pair = new Servers();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            pair.upstream = builder.Build();
            pair.upstream.Run(async ctx => { Interlocked.Increment(ref pair.calls); await handler(ctx); });
            await pair.upstream.StartAsync();
            pair.facade = FacadeApplication.Build([
                "--urls=http://127.0.0.1:0", "--Legacy:BaseUrl=" + Address(pair.upstream),
                "--Legacy:PublicServiceUrl=https://public.example/NfseWS.asmx", "--Legacy:TimeoutSeconds=" + timeoutSeconds,
                "--Logging:LogLevel:Default=None"
            ]);
            await pair.facade.StartAsync();
            pair.Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            {
                BaseAddress = new Uri(Address(pair.facade)), Timeout = TimeSpan.FromSeconds(30)
            };
            return pair;
        }

        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await facade.DisposeAsync();
            await upstream.DisposeAsync();
        }
    }
}
