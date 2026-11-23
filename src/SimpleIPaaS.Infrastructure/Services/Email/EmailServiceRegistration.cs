using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services.Email;

// Shared by the API (password resets, invites) and the Engine (failure alerts).
// Development without an SMTP host writes the message to the log instead of failing.
public static class EmailServiceRegistration
{
    public static IServiceCollection AddEmailSender(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection("Email:Smtp");
        var email = new EmailOptions
        {
            Host = section["Host"] ?? string.Empty,
            Port = int.TryParse(section["Port"], out var port) ? port : 587,
            UseSsl = !bool.TryParse(section["UseSsl"], out var useSsl) || useSsl,
            Username = section["Username"] ?? string.Empty,
            Password = section["Password"] ?? string.Empty,
            FromAddress = string.IsNullOrWhiteSpace(section["FromAddress"]) ? "no-reply@fusetor.com" : section["FromAddress"]!,
            FromName = string.IsNullOrWhiteSpace(section["FromName"]) ? "Fusetor" : section["FromName"]!
        };

        services.AddSingleton(email);

        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(email.Host))
        {
            services.AddScoped<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }

        return services;
    }
}
