using System.Text;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace NotaCarioca.Fachada;

public sealed record RoutingMetadata(string Provider, bool StrictCharacterDivergence);

public sealed class SoapRouting(RoutingConfiguration configuration, LegacyProxy proxy, ShadowConsultation shadow,
    ModernOptions modern, ProxyOptions legacy)
{
    public const string Namespace = "http://notacarioca.rio.gov.br/";
    public const string Action = Namespace + "ConsultarNfse";
    public const int MaxRequestBytes = 1_048_576;

    public async Task ForwardAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var operation = Telemetry.Operation(context.Request.Headers["SOAPAction"].ToString());
        context.Items["routing.mode"] = "LEGACY";
        Activity.Current?.SetTag("soap.operation", operation);
        try { await RouteAsync(context); }
        finally
        {
            var mode = (string)context.Items["routing.mode"]!;
            Activity.Current?.SetTag("routing.mode", mode);
            var tags = new TagList { { "soap.operation", operation }, { "routing.mode", mode } };
            Telemetry.Requests.Add(1, tags);
            Telemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            context.RequestServices.GetRequiredService<ILogger<SoapRouting>>().LogInformation(
                "SOAP {Operation} {RoutingMode} status={Status} TraceId={TraceId}",
                operation, mode, context.Response.StatusCode, Activity.Current?.TraceId.ToString());
        }
    }

    private async Task RouteAsync(HttpContext context)
    {
        var path = "/NfseWS.asmx" + context.Request.QueryString;
        var snapshot = configuration.Read();
        // Unknown actions, SOAP 1.2, compressed/chunked/large bodies and noncanonical queries stay opaque.
        if (snapshot.KillSwitch || !snapshot.Operations.TryGetValue("ConsultarNfse", out var rule) ||
            rule.Mode == "LEGACY" || rule.Percentage == 0 || !Candidate(context.Request))
        {
            await proxy.ForwardAsync(context, path);
            return;
        }

        var original = context.Request.Body;
        var elapsed = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(legacy.TimeoutSeconds));
        // Only a known bounded Content-Length is inspected; no buffering to disk or changes to request bytes.
        var bytes = new byte[(int)context.Request.ContentLength!.Value];
        try { await original.ReadExactlyAsync(bytes, budget.Token); }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested) context.Abort();
            else
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync("Tempo limite de leitura da consulta excedido.", context.RequestAborted);
            }
            return;
        }
        using var replay = new MemoryStream(bytes, writable: false);
        context.Request.Body = replay;
        try
        {
            var metadata = Inspect(bytes);
            // Re-read after upload/inspection so a kill switch changed during upload also takes effect.
            var mode = metadata is null ? RoutingMode.LEGACY : configuration.Read().Select(metadata.Provider);
            context.Items["routing.mode"] = mode.ToString();
            Activity.Current?.SetTag("routing.mode", mode.ToString());
            if (mode == RoutingMode.NEW)
            {
                // Include inspection/upload time in the selected NEW transport budget as well.
                var remaining = TimeSpan.FromSeconds(modern.TimeoutSeconds) - elapsed.Elapsed;
                if (remaining > TimeSpan.Zero) budget.CancelAfter(remaining);
                else budget.Cancel();
                // One attempt. An ambiguous modern outcome never triggers a legacy replay.
                await proxy.ForwardAsync(context, path, "modern", modern.TimeoutSeconds, budget: budget.Token);
                return;
            }
            if (mode == RoutingMode.SHADOW && shadow.TryReserve())
            {
                var capture = new ResponseCapture();
                var submitted = false;
                try
                {
                    await proxy.ForwardAsync(context, path, capture: capture, budget: budget.Token);
                    if (capture.Complete && !capture.Overflow && !context.RequestAborted.IsCancellationRequested)
                        submitted = shadow.Submit(new(bytes, context.Request.ContentType!, metadata!, capture, Activity.Current?.Context ?? default));
                }
                finally { if (!submitted) { Telemetry.ShadowResult("capture-or-queue"); shadow.Release(); } }
            }
            else await proxy.ForwardAsync(context, path, budget: budget.Token);
        }
        finally { context.Request.Body = original; }
    }

    private static bool Candidate(HttpRequest request)
    {
        var actions = request.Headers["SOAPAction"];
        var contentType = request.ContentType?.Split(';')[0].Trim();
        return HttpMethods.IsPost(request.Method) && request.QueryString.HasValue == false &&
            actions.Count == 1 && actions[0]?.Trim() is { } action && (action == Action || action == '"' + Action + '"') &&
            string.Equals(contentType, "text/xml", StringComparison.OrdinalIgnoreCase) &&
            !request.Headers.ContainsKey("Content-Encoding") && !request.Headers.ContainsKey("Transfer-Encoding") &&
            request.ContentLength is > 0 and <= MaxRequestBytes;
    }

    public static RoutingMetadata? Inspect(byte[] body)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(body).TrimStart('\uFEFF');
            var envelope = Parse(text);
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace ns = Namespace;
            if (envelope.Root?.Name != soap + "Envelope") return null;
            var bodies = envelope.Root.Elements(soap + "Body").ToArray();
            if (bodies.Length != 1) return null;
            var wrapper = bodies[0].Elements().SingleOrDefault();
            if (wrapper?.Name != ns + "ConsultarNfseRequest") return null;
            var input = wrapper.Elements().SingleOrDefault();
            if (input?.Name != ns + "inputXML" || input.HasElements ||
                envelope.Descendants().Count(e => e.Name.LocalName == "inputXML") != 1) return null;
            // CheckCharacters=false permits observing the existing documented &#1; divergence;
            // DTD/entity expansion stays prohibited and structure/depth remain bounded.
            var inner = Parse(input.Value, checkCharacters: false);
            if (inner.Root?.Name != "ConsultarNfseEnvio") return null;
            var providers = inner.Root.Elements("Prestador").ToArray();
            if (providers.Length != 1) return null;
            var registrations = providers[0].Elements("InscricaoMunicipal").ToArray();
            if (registrations.Length != 1 || registrations[0].HasElements) return null;
            var provider = registrations[0].Value.Trim();
            if (provider.Length is < 1 or > 32 || provider.Any(c => c is < '0' or > '9')) return null;
            var strict = inner.DescendantNodes().OfType<XText>().Any(t => t.Value.Any(c => c < 32 && c is not ('\t' or '\r' or '\n')));
            return new(provider, strict);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or DecoderFallbackException)
        { return null; }
    }

    internal static XDocument Parse(string xml, bool checkCharacters = true)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024, CheckCharacters = checkCharacters
        };
        using (var depth = XmlReader.Create(new StringReader(xml), settings))
            while (depth.Read())
                if (depth.Depth >= 64) throw new XmlException("XML excede limite de profundidade.");
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }
}
