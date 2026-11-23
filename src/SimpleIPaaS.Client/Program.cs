using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SimpleIPaaS.Client;
using SimpleIPaaS.Client.Services;
using Fluxor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
var apiUri = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var absoluteUri)
    && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
    ? absoluteUri
    : new Uri(new Uri(builder.HostEnvironment.BaseAddress), apiBaseUrl);

builder.Services.AddScoped(_ => new AuthApiClient(apiUri));
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<AuthTokenHandler>();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
builder.Services.AddAuthorizationCore();

builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthTokenHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler)
    {
        BaseAddress = apiUri,
        Timeout = TimeSpan.FromSeconds(210)
    };
});

builder.Services.AddScoped<SimpleIPaaS.Client.Services.AiBuildJobTracker>();
builder.Services.AddScoped<SimpleIPaaS.Client.Services.PageVisibilityService>();

builder.Services.AddFluxor(o => o
    .ScanAssemblies(typeof(Program).Assembly));

await builder.Build().RunAsync();
