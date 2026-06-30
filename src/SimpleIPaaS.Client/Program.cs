using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SimpleIPaaS.Client;
using Fluxor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Pointing to our local API (assuming it runs on port 5000/5001 usually, but for MVP let's point to localhost:5000 or base address if hosted together)
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("http://localhost:5000") });

builder.Services.AddFluxor(o => o
    .ScanAssemblies(typeof(Program).Assembly));

await builder.Build().RunAsync();
