using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Olve.Pipelines.Configuration;

public static class AuthenticationConfiguration
{
    /// <summary>Policy of the API's reads: a signed-in user, unless <c>Auth:AnonymousReads</c> opens them (beta).</summary>
    public const string ReadPolicy = "read";

    /// <summary>Reads of pipeline state (pipelines, runs, logs, outputs, events) require <see cref="ReadPolicy"/>.</summary>
    public static RouteHandlerBuilder RequireRead(this RouteHandlerBuilder endpoint) => endpoint.RequireAuthorization(ReadPolicy);

    public static void ConfigureAuthentication(this WebApplicationBuilder builder)
    {
        var disableAuth = builder.Configuration.GetValue<bool>("Auth:Disable");
        var anonymousReads = disableAuth || builder.Configuration.GetValue<bool>("Auth:AnonymousReads");

        if (disableAuth)
        {
            builder.Services.AddAuthentication().AddJwtBearer();
            builder.Services.AddAuthorizationBuilder().AddPolicy(ReadPolicy, p => p.RequireAssertion(_ => true));
            return;
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var authority = builder.Configuration["Auth:Authority"];
                var audience = builder.Configuration["Auth:Audience"];
                var signingKey = builder.Configuration["Auth:SigningKey"];

                options.Authority = authority;
                options.Audience = audience;

                var frontendAuthority = builder.Configuration["Auth:FrontendAuthority"];
                var frontendAudience = builder.Configuration["Auth:FrontendAudience"];

                var validIssuers = new List<string>();
                if (authority is not null) validIssuers.Add(authority);
                if (frontendAuthority is not null) validIssuers.Add(frontendAuthority);

                var validAudiences = new List<string>();
                if (audience is not null) validAudiences.Add(audience);
                if (frontendAudience is not null) validAudiences.Add(frontendAudience);

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = validIssuers.Count > 0,
                    ValidIssuers = validIssuers,
                    ValidateAudience = validAudiences.Count > 0,
                    ValidAudiences = validAudiences,
                    ValidateLifetime = true,
                };

                // A plain-HTTP OIDC provider (the local compose stack's Dex) needs Auth:RequireHttpsMetadata=false.
                if (!builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true))
                    options.RequireHttpsMetadata = false;

                if (builder.Environment.IsDevelopment())
                {
                    options.RequireHttpsMetadata = false;
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                }

                if (signingKey is not null)
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                }
            });

        var allow = AuthAllowList.From(builder.Configuration);
        var signedIn = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => allow.Allows(ctx.User) || Refused(ctx))
            .Build();

        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(signedIn)
            .SetFallbackPolicy(signedIn)
            .AddPolicy(ReadPolicy, p =>
            {
                if (anonymousReads)
                    p.RequireAssertion(_ => true);
                else
                    p.Combine(signedIn);
            });
    }

    // Logs the stable subject id, so an admin can put it in Auth:Allow:Subjects.
    private static bool Refused(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.Resource is HttpContext http)
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthenticationConfiguration))
                .LogWarning("Refused '{Username}' (subject '{Subject}'): not in Auth:Allow",
                    ctx.User.FindFirst("preferred_username")?.Value, AuthAllowList.Subject(ctx.User));
        return false;
    }

    public static void MapAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
