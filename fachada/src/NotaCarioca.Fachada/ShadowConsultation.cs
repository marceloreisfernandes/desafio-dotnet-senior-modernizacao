using System.Diagnostics;
using OpenTelemetry.Context.Propagation;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;

namespace NotaCarioca.Fachada;

public sealed class ResponseCapture
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private readonly MemoryStream body = new();
    public int Status { get; private set; }
    public string? ContentType { get; private set; }
    public bool Overflow { get; private set; }
    public bool Complete { get; set; }
    public byte[] Bytes => body.ToArray();
    public void Start(HttpResponseMessage response)
    {
        Status = (int)response.StatusCode;
        ContentType = response.Content.Headers.ContentType?.ToString();
        if (response.Content.Headers.ContentEncoding.Count != 0) Overflow = true;
    }
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (Overflow) return;
        if (body.Length + bytes.Length > MaxBytes)
        {
            Overflow = true;
            body.SetLength(0);
            return;
        }
        body.Write(bytes);
    }
}

public sealed record ShadowWork(byte[] Request, string ContentType, RoutingMetadata Metadata, ResponseCapture Legacy, ActivityContext Parent = default);

// At most N reservations (including legacy requests still running), N workers and N queue entries.
// No Task.Run per request, no retries, no orphaned work when a deadline expires.
public sealed class ShadowConsultation : BackgroundService
{
    private readonly Channel<ShadowWork> queue;
    private readonly SemaphoreSlim slots;
    private readonly IHttpClientFactory clients;
    private readonly ModernOptions options;
    private readonly ILogger<ShadowConsultation> logger;

    public ShadowConsultation(IHttpClientFactory clients, ModernOptions options, ILogger<ShadowConsultation> logger)
    {
        this.clients = clients;
        this.options = options;
        this.logger = logger;
        slots = new(options.ShadowConcurrency, options.ShadowConcurrency);
        queue = Channel.CreateBounded<ShadowWork>(new BoundedChannelOptions(options.ShadowConcurrency)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = options.ShadowConcurrency == 1 });
    }

    public bool TryReserve()
    {
        var reserved = slots.Wait(0);
        if (!reserved) Telemetry.ShadowResult("capacity-skipped");
        if (!reserved) logger.LogInformation("Sombra ConsultarNfse: {Resultado}", "capacity-skipped");
        return reserved;
    }
    public void Release() => slots.Release();
    public bool Submit(ShadowWork work) => queue.Writer.TryWrite(work);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, options.ShadowConcurrency).Select(_ => Consume(stoppingToken)));

    private async Task Consume(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var span = Telemetry.Source.StartActivity("shadow ConsultarNfse", ActivityKind.Internal, work.Parent);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(options.ShadowTimeoutMilliseconds);
                    using var client = clients.CreateClient("modern");
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/NfseWS.asmx");
                    request.Headers.TryAddWithoutValidation("SOAPAction", '"' + SoapRouting.Action + '"');
                    request.Content = new ByteArrayContent(work.Request);
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", work.ContentType);
                    // Only W3C trace context crosses the worker boundary; no baggage or caller credentials.
                    using var http = Telemetry.Source.StartActivity("HTTP modern", ActivityKind.Client);
                    http?.SetTag("upstream", "modern");
                    new TraceContextPropagator().Inject(new PropagationContext(Activity.Current?.Context ?? default, default),
                        request, (message, key, value) => message.Headers.TryAddWithoutValidation(key, value));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    http?.SetTag("http.response.status_code", (int)response.StatusCode);
                    var capture = new ResponseCapture();
                    capture.Start(response);
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var buffer = new byte[8192];
                    int count;
                    while (!capture.Overflow && (count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
                        capture.Append(buffer.AsSpan(0, count));
                    var result = capture.Overflow ? "response-limit" : Compare(work.Metadata, work.Legacy, capture);
                    Telemetry.ShadowResult(result);
                    logger.LogInformation("Sombra ConsultarNfse: {Resultado}", result);
                }
                catch (OperationCanceledException)
                { Telemetry.ShadowResult("timeout-or-stop"); span?.SetStatus(ActivityStatusCode.Error); logger.LogInformation("Sombra ConsultarNfse: {Resultado}", "timeout-or-stop"); }
                catch (Exception)
                {
                    Telemetry.ShadowResult("failure");
                    span?.SetStatus(ActivityStatusCode.Error);
                    // Observe every worker failure, without exception messages, identifiers or fiscal data.
                    logger.LogWarning("Sombra ConsultarNfse: {Resultado}", "failure");
                }
                finally { Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { while (queue.Reader.TryRead(out _)) { Telemetry.ShadowResult("stop"); Release(); } }
    }

    public static string Compare(RoutingMetadata metadata, ResponseCapture legacy, ResponseCapture modern)
    {
        var original = legacy.Bytes;
        var candidate = modern.Bytes;
        if (legacy.Status == modern.Status && legacy.ContentType == modern.ContentType && original.AsSpan().SequenceEqual(candidate))
            return "parity";
        // Narrow exception: a documented strict-character rejection must be E001 vs successful invoice XML.
        // Other differences (including status, values, ordering or extra errors) remain unexpected.
        if (metadata.StrictCharacterDivergence && legacy.Status == 200 && modern.Status == 200 &&
            legacy.ContentType == modern.ContentType)
        {
            try
            {
                XNamespace ns = SoapRouting.Namespace;
                var before = SoapRouting.Parse(System.Text.Encoding.UTF8.GetString(original)).Descendants(ns + "outputXML").Single().Value;
                var after = SoapRouting.Parse(System.Text.Encoding.UTF8.GetString(candidate)).Descendants(ns + "outputXML").Single().Value;
                const string expected = "<ConsultarNfseResposta><ListaMensagemRetorno><MensagemRetorno><Codigo>E001</Codigo>" +
                    "<Mensagem>XML mal formado ou inválido.</Mensagem><Correcao>Verifique a estrutura do XML enviado.</Correcao>" +
                    "</MensagemRetorno></ListaMensagemRetorno></ConsultarNfseResposta>";
                if (after == expected && SoapRouting.Parse(before).Descendants("CompNfse").Any())
                    return "expected-strict-xml";
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException) { }
        }
        return "unexpected-divergence";
    }
}
