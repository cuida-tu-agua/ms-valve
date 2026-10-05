using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace SyWater.Valve.Api.Security;

/// <summary>
/// Validates the access tokens issued by ms-iam (RS256). valve-service only needs the
/// PUBLIC key: it can verify signatures but can never issue tokens.
/// After the signature check, the token is also looked up in the ms-iam denylist (Redis).
/// </summary>
public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddIamJwtAuthentication(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var section = config.GetSection("Jwt");
        var issuer = section["Issuer"] ?? throw new InvalidOperationException("Missing Jwt:Issuer.");
        var keyPath = section["PublicKeyPath"] ?? throw new InvalidOperationException("Missing Jwt:PublicKeyPath.");

        var fullPath = Path.IsPathRooted(keyPath) ? keyPath : Path.Combine(AppContext.BaseDirectory, keyPath);
        var rsa = RSA.Create(); // not disposed on purpose: the key lives as long as the app
        rsa.ImportFromPem(File.ReadAllText(fullPath));

        var redisConfiguration = config["Redis:Configuration"];
        if (string.IsNullOrWhiteSpace(redisConfiguration))
            throw new InvalidOperationException("Missing Redis:Configuration (the same Redis that ms-iam uses).");

        // AbortOnConnectFail = false: the API starts even if Redis is down; requests then get 401 (fail closed)
        var redisOptions = ConfigurationOptions.Parse(redisConfiguration);
        redisOptions.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
        services.AddSingleton<ITokenRevocationChecker, RedisTokenRevocationChecker>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = !env.IsDevelopment();
                options.MapInboundClaims = false; // keep "sub" as "sub" (no SOAP-style claim names)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = false, // ms-iam does not emit "aud" yet (see guide)
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = section["KeyId"] },
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = "sub",
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedTokens };
            });

        return services;
    }

    /// <summary>Runs only for tokens whose signature, issuer and expiration are already valid.</summary>
    private static async Task RejectRevokedTokens(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var tokenId = principal?.FindFirstValue("jti");
        var userId = principal?.FindFirstValue("sub");
        long? issuedAt = long.TryParse(principal?.FindFirstValue("iat"), out var iat) ? iat : null;

        var checker = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationChecker>();
        try
        {
            if (await checker.IsRevokedAsync(tokenId, userId, issuedAt))
                context.Fail("The token was revoked.");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Same rule as ms-iam: if we cannot check the denylist, we do not trust the token
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SyWater.Valve.Api.Security.TokenRevocation")
                .LogError(ex, "Redis is not available: the token cannot be checked, the request is rejected.");
            context.Fail("The token could not be checked.");
        }
    }
}
