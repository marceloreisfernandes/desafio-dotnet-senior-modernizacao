using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using Oracle.ManagedDataAccess.Client;
using NotaCarioca.Consulta;

await ConsultaApplication.Build(args).RunAsync();

namespace NotaCarioca.Consulta
{
    public static class ConsultaApplication
    {
        public static WebApplication Build(string[] args,
            Func<ConsultaSolicitada, CancellationToken, Task<ResultadoConsulta>>? executar = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            Telemetry.Configure(builder);
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            var connectionString = builder.Configuration["NFSE_CONNECTION_STRING"];
            if (executar is null && string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("NFSE_CONNECTION_STRING é obrigatória.");
            var oracle = new OracleConsulta(connectionString ?? "");
            executar ??= oracle.ExecutarAsync;
            var soap = new SoapAdapter(executar);
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
                { await RestApi.Problem(413, "requisicao-grande", "A requisição excede 1 MiB.").ExecuteAsync(context); }
                catch (Exception ex)
                {
                    var unavailable = ex is OracleException or TimeoutException;
                    // Do not log SQL, connection strings, request queries, documents or exception text.
                    app.Logger.LogWarning("Consulta falhou; categoria={Categoria} traceId={TraceId}",
                        unavailable ? "dependencia" : "interno", System.Diagnostics.Activity.Current?.TraceId.ToString());
                    if (context.Request.Path == "/NfseWS.asmx")
                    {
                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "text/xml; charset=utf-8";
                        await context.Response.WriteAsync(SoapAdapter.Fault("soap:Server", "Erro interno no processamento."));
                    }
                    else await RestApi.Problem(unavailable ? 503 : 500, unavailable ? "oracle-indisponivel" : "erro-interno",
                        unavailable ? "Consulta temporariamente indisponível." : "Erro interno no processamento.").ExecuteAsync(context);
                }
            });
            app.MapGet("/health/live", () => Results.Text("OK"));
            app.MapGet("/health/ready", async (HttpContext context) =>
            {
                try { return await oracle.PingAsync(context.RequestAborted) ? Results.Text("OK") : Results.StatusCode(503); }
                catch (OracleException) { return Results.StatusCode(503); }
            });
            app.MapGet("/api/v1/prestadores/{inscricaoMunicipal}/nfse", async (HttpContext context) =>
                await (await RestApi.ConsultarAsync(context, executar)).ExecuteAsync(context));
            app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json"));
            app.MapGet("/NfseWS.asmx", (HttpContext context) => context.Request.Query.ContainsKey("wsdl")
                ? Results.File(Path.Combine(AppContext.BaseDirectory, "ConsultarNfse.wsdl"), "text/xml; charset=utf-8")
                : Results.Text("ConsultarNfse — serviço interno de consulta."));
            app.MapPost("/NfseWS.asmx", async (HttpContext context) =>
            {
                context.Response.ContentType = "text/xml; charset=utf-8";
                var action = context.Request.Headers["SOAPAction"].ToString().Trim().Trim('"');
                var operation = action[(action.LastIndexOf('/') + 1)..];
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                string input;
                try { input = SoapAdapter.LerInput(body); }
                catch (XmlException)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync(SoapAdapter.Fault("soap:Client", "Envelope SOAP inválido"));
                    return;
                }
                if (operation != "ConsultarNfse")
                {
                    context.Response.StatusCode = 500;
                    // No dispatch path for emissions, cancellations, or other legacy operations.
                    await context.Response.WriteAsync(SoapAdapter.Fault("soap:Client", "Operação desconhecida: " + operation));
                    return;
                }
                var output = await soap.ConsultarAsync(input, context.RequestAborted);
                await context.Response.WriteAsync(SoapAdapter.Envelope(output), context.RequestAborted);
            });
            return app;
        }
    }
}
