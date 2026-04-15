using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Olve.Pipelines.Configuration;

public static class AuthenticationConfiguration
{
    public static void ConfigureAuthentication(this WebApplicationBuilder builder)
    {
        var disableAuth = builder.Configuration.GetValue<bool>("Auth:Disable");

        if (disableAuth)
        {
            builder.Services.AddAuthentication().AddJwtBearer();
            builder.Services.AddAuthorization();
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

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(
                new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build());
    }

    public static void MapAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
