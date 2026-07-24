using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SimpleIPaaS.Client;
using Fluxor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5000";
var apiKey = builder.Configuration["ApiKey"] ?? "dev-api-key";

builder.Services.AddScoped(sp =>
{
    var client = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    return client;
});

builder.Services.AddFluxor(o => o
    .ScanAssemblies(typeof(Program).Assembly));

await builder.Build().RunAsync();
