namespace NotaCarioca.Fachada;

public sealed class ProxyOptions
{
    public string BaseUrl { get; set; } = "http://webservices:8080";
    public string PublicServiceUrl { get; set; } = "http://localhost:8080/NfseWS.asmx";
    // Preserve the lot tool's 600-second client budget with 60 seconds of headroom.
    public int TimeoutSeconds { get; set; } = 660;

    public void Validate()
    {
        if (!IsHttpUrl(BaseUrl, out var upstream) || upstream!.AbsolutePath != "/" ||
            upstream.Query.Length != 0 || upstream.Fragment.Length != 0)
            throw new InvalidOperationException("Legacy:BaseUrl deve ser uma origem HTTP(S).");
        if (!IsHttpUrl(PublicServiceUrl, out var service) || service!.AbsolutePath != "/NfseWS.asmx" ||
            service.Query.Length != 0 || service.Fragment.Length != 0)
            throw new InvalidOperationException("Legacy:PublicServiceUrl deve apontar para /NfseWS.asmx.");
        if (TimeoutSeconds is < 1 or > 660)
            throw new InvalidOperationException("Legacy:TimeoutSeconds deve estar entre 1 e 660.");
    }

    private static bool IsHttpUrl(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
}
