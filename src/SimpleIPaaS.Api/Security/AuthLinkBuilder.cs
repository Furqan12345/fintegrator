using Microsoft.Extensions.Hosting;

namespace SimpleIPaaS.Api.Security;

public class AuthLinkBuilder
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AuthLinkBuilder(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public string BuildResetLink(HttpRequest request, string email, string token, bool invite)
    {
        var configured = _configuration["App:PublicBaseUrl"];
        string baseUrl;
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            baseUrl = configured.TrimEnd('/');
        }
        else if (_environment.IsDevelopment())
        {
            baseUrl = $"{request.Scheme}://{request.Host}";
        }
        else
        {
            throw new InvalidOperationException("App:PublicBaseUrl must be an absolute http(s) URL in production.");
        }

        var url = $"{baseUrl}/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        return invite ? url + "&invite=1" : url;
    }
}
