using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotaCarioca.Fachada;

public enum RoutingMode { LEGACY, SHADOW, NEW }

// Transport/capacity settings are startup-only. Traffic decisions belong exclusively to the local file.
public sealed class ModernOptions
{
    public string BaseUrl { get; set; } = "http://consulta:8080";
    public int TimeoutSeconds { get; set; } = 30;
    public int ShadowTimeoutMilliseconds { get; set; } = 2000;
    public int ShadowConcurrency { get; set; } = 2;

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            TimeoutSeconds is < 1 or > 300 || ShadowTimeoutMilliseconds is < 50 or > 10000 ||
            ShadowConcurrency is < 1 or > 8)
            throw new InvalidOperationException("Configuração de transporte/capacidade da consulta inválida.");
    }
}

public sealed class RoutingConfiguration(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public sealed record Rule
    {
        // Exact names only: the general enum converter also accepts composite strings such as "LEGACY, NEW".
        public required string Mode { get; init; }
        public required int Percentage { get; init; }
        // Empty means all providers. Values are opaque exact routing metadata, never authorization.
        public required string[] Providers { get; init; }
    }

    public sealed record Snapshot
    {
        public required bool KillSwitch { get; init; }
        public required Dictionary<string, Rule> Operations { get; init; }

        public RoutingMode Select(string provider)
        {
            if (KillSwitch || !Operations.TryGetValue("ConsultarNfse", out var rule) || rule.Mode == "LEGACY" ||
                (rule.Providers.Length != 0 && !rule.Providers.Contains(provider, StringComparer.Ordinal)) ||
                Bucket(provider) >= rule.Percentage) return RoutingMode.LEGACY;
            return rule.Mode == "SHADOW" ? RoutingMode.SHADOW : RoutingMode.NEW;
        }
    }

    // Read a complete bounded snapshot on every decision. No last-known-good permissive state.
    public Snapshot Read()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 16384) return Legacy();
            var bytes = new byte[16385];
            var count = 0;
            int read;
            while (count < bytes.Length && (read = file.Read(bytes, count, bytes.Length - count)) != 0) count += read;
            if (count > 16384) return Legacy();
            // Reject duplicate properties, including conflicting kill switches/rules.
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            if (HasDuplicates(document.RootElement)) return Legacy();
            var snapshot = JsonSerializer.Deserialize<Snapshot>(bytes.AsSpan(0, count), Json);
            if (snapshot?.Operations is null || snapshot.Operations.Any(pair =>
                pair.Key != "ConsultarNfse" || pair.Value is null || pair.Value.Mode is not ("LEGACY" or "SHADOW" or "NEW") ||
                pair.Value.Percentage is < 0 or > 100 || pair.Value.Providers is null ||
                pair.Value.Providers.Any(p => string.IsNullOrEmpty(p) || p.Length > 32 || p.Any(c => c is < '0' or > '9'))))
                return Legacy();
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Legacy(); }
    }

    private static bool HasDuplicates(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
            element.EnumerateObject().Count() || element.EnumerateObject().Any(p => HasDuplicates(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicates),
        _ => false
    };

    private static Snapshot Legacy() => new() { KillSwitch = true, Operations = new() };

    // Stable across processes, runtimes and restarts. Never log/store this digest or the provider.
    public static int Bucket(string provider) => (int)(BinaryPrimitives.ReadUInt32BigEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes("ConsultarNfse:" + provider))) % 100);
}
