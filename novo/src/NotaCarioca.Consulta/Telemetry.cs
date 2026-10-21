using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace NotaCarioca.Consulta;

// Native Activity/Meter instruments and the standard SDK; no request content is collected.
public static class Telemetry
{
    public const string Name = "nfse-consulta";
    public static readonly ActivitySource Source = new(Name);

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
            .WithMetrics(m => m.AddMeter(Name).AddOtlpExporter());
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
