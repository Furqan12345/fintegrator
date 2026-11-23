using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Security;
using SimpleIPaaS.Infrastructure.Services.Email;

namespace SimpleIPaaS.Api.Security;

public static class AuthRegistration
{
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var jwt = configuration.GetSection("Auth:Jwt").Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            if (environment.IsDevelopment())
            {
                jwt.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            }
            else
            {
                throw new InvalidOperationException("Auth:Jwt:SigningKey must be configured with at least 32 characters outside Development.");
            }
        }

        if (jwt.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Auth:Jwt:SigningKey must be at least 32 characters.");
        }

        services.AddSingleton(jwt);
        services.AddHttpContextAccessor();

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 8;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedEmail = false;
                options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddEntityFrameworkStores<IPaaSContext>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(1));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = TokenService.CreateValidationParameters(jwt);
            });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
            options.AddPolicy("Admin", policy => policy.RequireRole(AppRoles.Admin));
            options.AddPolicy("CanEdit", policy => policy.RequireRole(AppRoles.Admin, AppRoles.User));
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("webhook", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("test-mapping", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("alert-test", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.Name
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        services.AddEmailSender(configuration, environment);

        services.AddScoped<TokenService>();
        services.AddScoped<AuthAuditService>();
        services.AddScoped<AuthLinkBuilder>();

        return services;
    }
}
