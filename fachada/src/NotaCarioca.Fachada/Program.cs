using System.Net;
using NotaCarioca.Fachada;

await FacadeApplication.Build(args).RunAsync();

namespace NotaCarioca.Fachada
{
    public static class FacadeApplication
    {
        public static WebApplication Build(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            Telemetry.Configure(builder);
            // Match the legacy host: large RPS batches must remain accepted.
            builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
            // Framework request logs can contain query strings and sensitive headers.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

            var options = new ProxyOptions();
            builder.Configuration.GetSection("Legacy").Bind(options);
            options.Validate();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<LegacyProxy>();
            var modern = new ModernOptions();
            builder.Configuration.GetSection("Modern").Bind(modern);
            modern.Validate();
            builder.Services.AddSingleton(modern);
            builder.Services.AddSingleton(new RoutingConfiguration(Path.GetFullPath(
                builder.Configuration["Routing:File"] ?? "routing.json", builder.Environment.ContentRootPath)));
            builder.Services.AddSingleton<SoapRouting>();
            builder.Services.AddSingleton<ShadowConsultation>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ShadowConsultation>());
            builder.Services.AddHttpClient("modern", client =>
            {
                client.BaseAddress = new Uri(modern.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
                AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }); // The modern consultation also has exactly one attempt.
            builder.Services.AddHttpClient("legacy", client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl);
                // The linked token also bounds response body streaming.
                client.Timeout = Timeout.InfiniteTimeSpan;
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }); // No retry/resilience handler: a SOAP POST is sent once.

            var app = builder.Build();
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                foreach (var result in new[] { "parity", "expected-divergence", "unexpected-divergence", "dropped" })
                    Telemetry.Shadow.Add(0, new KeyValuePair<string, object?>("shadow.result", result));
            });
            app.MapGet("/health/live", () => Results.Text("OK"));
            app.MapGet("/health", (HttpContext ctx, LegacyProxy proxy) => proxy.ForwardAsync(ctx, "/health"));
            app.MapGet("/health/ready", (HttpContext ctx, LegacyProxy proxy) => proxy.ForwardAsync(ctx, "/health"));
            app.Map("/NfseWS.asmx", (HttpContext ctx, SoapRouting routing) => routing.ForwardAsync(ctx));
            return app;
        }
    }
}
