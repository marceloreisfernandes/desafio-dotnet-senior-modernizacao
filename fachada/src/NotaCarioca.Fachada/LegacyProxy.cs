using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Primitives;

namespace NotaCarioca.Fachada;

public sealed class LegacyProxy(IHttpClientFactory clients, ProxyOptions options, ILogger<LegacyProxy> logger)
{
    private static HashSet<string> HopHeaders(IEnumerable<string> connectionValues)
    {
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Proxy-Connection"
        };
        foreach (var value in connectionValues)
            foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                headers.Add(name);
        return headers;
    }

    public async Task ForwardAsync(HttpContext context, string pathAndQuery,
        string upstream = "legacy", int? timeoutSeconds = null, ResponseCapture? capture = null,
        CancellationToken budget = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, budget);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? options.TimeoutSeconds));
        var token = deadline.Token;
        var wsdl = HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path.Equals("/NfseWS.asmx", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.Equals("/NfseWS.asmx/", StringComparison.OrdinalIgnoreCase)) &&
            context.Request.Query.ContainsKey("wsdl");
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), pathAndQuery);
            var excluded = HopHeaders(context.Request.Headers.Connection.Select(v => v ?? ""));
            excluded.Add("Host"); // The transport addresses the configured upstream, never a client supplied host.
            if (wsdl)
            {
                // The public WSDL has a different representation and validators.
                excluded.UnionWith(["Accept-Encoding", "If-None-Match", "If-Modified-Since", "Range", "If-Range"]);
            }
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Request.ContentType is not null)
                request.Content = new StreamContent(context.Request.Body);

            foreach (var header in context.Request.Headers)
            {
                if (excluded.Contains(header.Key)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using var client = clients.CreateClient(upstream);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            // Only WSDL is reserialized. Optional bounded shadow capture preserves the SOAP bytes.
            byte[]? publicWsdl = null;
            if (wsdl && response.IsSuccessStatusCode)
                publicWsdl = await RewriteWsdlAsync(response.Content, token);

            context.Response.StatusCode = (int)response.StatusCode;
            capture?.Start(response);
            var responseExcluded = HopHeaders(response.Headers.TryGetValues("Connection", out var values) ? values : []);
            if (publicWsdl is not null)
                responseExcluded.UnionWith(["Content-Length", "ETag", "Last-Modified", "Content-MD5", "Digest", "Content-Encoding"]);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!responseExcluded.Contains(header.Key))
                    context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());

            if (publicWsdl is not null)
            {
                context.Response.ContentLength = publicWsdl.Length;
                await context.Response.Body.WriteAsync(publicWsdl, token);
            }
            else if (capture is not null)
            {
                await using var body = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[8192];
                int count;
                while ((count = await body.ReadAsync(buffer, token)) != 0)
                {
                    capture.Append(buffer.AsSpan(0, count));
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), token);
                }
                capture.Complete = true;
            }
            else
                await response.Content.CopyToAsync(context.Response.Body, token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (OperationCanceledException)
        {
            await FailAsync(context, StatusCodes.Status504GatewayTimeout, upstream == "legacy" ?
                "Tempo limite do legado excedido." : "Tempo limite da consulta excedido.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or InvalidDataException)
        {
            await FailAsync(context, StatusCodes.Status502BadGateway, upstream == "legacy" ?
                "Falha de comunicação com o legado." : "Falha de comunicação com a consulta.");
        }
    }

    private async Task<byte[]> RewriteWsdlAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("WSDL comprimido inesperado.");
        await using var stream = await content.ReadAsStreamAsync(token);
        // XmlReader does not pass our cancellation token to every underlying read.
        // Read the bounded document explicitly so a stalled WSDL body also times out.
        using var document = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (document.Length + count > 4 * 1024 * 1024)
                throw new InvalidDataException("WSDL excede o limite de bytes.");
            await document.WriteAsync(buffer.AsMemory(0, count), token);
        }
        document.Position = 0;
        using var reader = XmlReader.Create(document, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024
        });
        var wsdl = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, token);
        XNamespace ns = "http://schemas.xmlsoap.org/wsdl/soap/";
        var addresses = wsdl.Descendants(ns + "address").ToArray();
        if (addresses.Length == 0) throw new InvalidDataException("WSDL sem endereço SOAP.");
        foreach (var address in addresses) address.SetAttributeValue("location", options.PublicServiceUrl);
        using var output = new MemoryStream();
        await wsdl.SaveAsync(output, SaveOptions.DisableFormatting, token);
        return output.ToArray();
    }

    private async Task FailAsync(HttpContext context, int status, string message)
    {
        // No exceptions, URLs, SOAPAction, headers or payloads in the log.
        logger.LogWarning("Falha no encaminhamento SOAP. HTTP {StatusCode}", status);
        if (context.Response.HasStarted)
        {
            // A partial SOAP response must fail at the transport, never look like a complete success.
            context.Abort();
            return;
        }
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
