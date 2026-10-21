using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace NotaCarioca.Fachada;

// Native Activity/Meter instruments and the standard SDK; no request content is collected.
public static class Telemetry
{
    public const string Name = "nfse-fachada";
    public static readonly ActivitySource Source = new(Name);
    private static readonly System.Diagnostics.Metrics.Meter Meter = new(Name);
    public static readonly System.Diagnostics.Metrics.Counter<long> Requests = Meter.CreateCounter<long>("nfse.soap.requests", "{request}");
    public static readonly System.Diagnostics.Metrics.Histogram<double> Duration = Meter.CreateHistogram<double>("nfse.soap.duration", "s");
    public static readonly System.Diagnostics.Metrics.Counter<long> Shadow = Meter.CreateCounter<long>("nfse.shadow.observations", "{observation}");

    public static string Operation(string action)
    {
        var value = action.Trim().Trim('"');
        var operation = value[(value.LastIndexOf('/') + 1)..];
        return operation is "ConsultarNfse" or "ConsultarNfseServicoTomado" or "ConsultarLoteRps" or
            "ConsultarSituacaoLoteRps" or "RecepcionarLoteRps" or "CancelarNfse" ? operation : "other";
    }

    public static void ShadowResult(string result)
    {
        var outcome = result switch
        {
            "parity" => "parity", "expected-strict-xml" => "expected-divergence",
            "unexpected-divergence" => "unexpected-divergence", _ => "dropped"
        };
        Shadow.Add(1, new KeyValuePair<string, object?>("shadow.result", outcome));
        Activity.Current?.SetTag("shadow.result", outcome);
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);
        builder.Services.Configure<LoggerFactoryOptions>(o =>
            o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(Name));
            o.AddOtlpExporter();
        });
        builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(Name))
            .WithTracing(t => t.AddSource(Name)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = c => c.Request.Path == "/NfseWS.asmx";
                    o.RecordException = false;
                })
                .AddProcessor(new SafeTags()).AddOtlpExporter())
            .WithMetrics(m => m.AddMeter(Name)
                .AddView("nfse.soap.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75,
                        1, 2.5, 5, 7.5, 10, 30, 60, 120, 300, 660]
                }).AddOtlpExporter());
    }

    // Remove automatic URLs, queries, peer names and error text before the batch exporter.
    // Only fixed application labels and numeric HTTP status are exported.
    private sealed class SafeTags : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (activity.Kind == ActivityKind.Server) activity.DisplayName = "SOAP";
            foreach (var tag in activity.TagObjects.ToArray())
                if (tag.Key is not ("soap.operation" or "routing.mode" or "upstream" or
                    "shadow.result" or "db.system.name" or "http.response.status_code"))
                    activity.SetTag(tag.Key, null);
            if (activity.Status == ActivityStatusCode.Error) activity.SetStatus(ActivityStatusCode.Error);
        }
    }
}
