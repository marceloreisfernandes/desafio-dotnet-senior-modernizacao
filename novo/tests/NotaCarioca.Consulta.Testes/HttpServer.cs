using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace NotaCarioca.Consulta.Testes;

internal sealed class HttpServer(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;

    public static async Task<HttpServer> Start(Func<ConsultaSolicitada, CancellationToken, Task<ResultadoConsulta>> execute)
    {
        var app = ConsultaApplication.Build(["--environment", "Testing"], execute);
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(45) });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
