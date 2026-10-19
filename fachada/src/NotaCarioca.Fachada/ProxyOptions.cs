namespace NotaCarioca.Fachada;

public sealed class ProxyOptions
{
    public string BaseUrl { get; set; } = "http://webservices:8080";
    public string PublicServiceUrl { get; set; } = "http://localhost:8080/NfseWS.asmx";
    public int TimeoutSeconds { get; set; } = 120;

    public void Validate()
    {
        if (!IsHttpUrl(BaseUrl, out var upstream) || upstream!.AbsolutePath != "/" ||
            upstream.Query.Length != 0 || upstream.Fragment.Length != 0)
            throw new InvalidOperationException("Legacy:BaseUrl deve ser uma origem HTTP(S).");
        if (!IsHttpUrl(PublicServiceUrl, out var service) || service!.AbsolutePath != "/NfseWS.asmx" ||
            service.Query.Length != 0 || service.Fragment.Length != 0)
            throw new InvalidOperationException("Legacy:PublicServiceUrl deve apontar para /NfseWS.asmx.");
        if (TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Legacy:TimeoutSeconds deve estar entre 1 e 300.");
    }

    private static bool IsHttpUrl(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
}
