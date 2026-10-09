using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SyWater.Valve.Api.Security;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Valves;
using SyWater.Valve.Infrastructure.Persistence;
using SyWater.Valve.Infrastructure.Persistence.Entities;

namespace SyWater.Valve.Api.Tests;

/// <summary>SQLite has no NEWID() nor N'' literals: only for the tests.</summary>
public sealed class SqliteValveDbContext(DbContextOptions options) : ValveDbContext(options)
{
    protected override string PendingIndexFilter => "[status] = 'SENT'";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ValveStateEntity>().Property(v => v.Id).HasDefaultValueSql(null).ValueGeneratedOnAdd();
    }
}

public sealed class FakePlaceAccess : IPlaceAccessChecker
{
    public HashSet<Guid> Mine { get; } = [];
    public Task<bool> CanManageAsync(Guid placeId, CancellationToken ct) => Task.FromResult(Mine.Contains(placeId));
}

public sealed class FakeCodeVerifier : IConfirmationCodeVerifier
{
    public Task VerifyAsync(string action, string code, CancellationToken ct) =>
        code == "123456" ? Task.CompletedTask : throw new ConfirmationCodeRejectedException("auth.invalid_code", 4);
}

public sealed class FakeDeviceGateway : IDeviceGateway
{
    public bool Offline { get; set; }
    public List<(Guid CommandId, ValveAction Action)> Sent { get; } = [];
    public Task<DateTime> SendAsync(Guid placeId, Guid commandId, ValveAction action, CancellationToken ct)
    {
        if (Offline) throw new ValveDeviceOfflineException();
        lock (Sent) Sent.Add((commandId, action));
        return Task.FromResult(DateTime.UtcNow);
    }
}

public sealed class NoRevocations : ITokenRevocationChecker
{
    public Task<bool> IsRevokedAsync(string? tokenId, string? userId, long? issuedAt) => Task.FromResult(false);
}

/// <summary>The real API (Program.cs) with SQLite, a test RSA key and fake ms-places / ms-iam / ms-devices / Redis.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://iam.cuidatuagua.local";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly string _publicKeyPath = Path.Combine(Path.GetTempPath(), $"iam-public-{Guid.NewGuid():N}.pem");

    public FakePlaceAccess Places { get; } = new();
    public FakeDeviceGateway Devices { get; } = new();

    public ApiFactory()
    {
        _sqlite.Open();
        File.WriteAllText(_publicKeyPath, _rsa.ExportSubjectPublicKeyInfoPem());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Valve", "unused-in-tests");
        builder.UseSetting("Jwt:PublicKeyPath", _publicKeyPath);
        builder.UseSetting("Internal:ApiKey", "test-internal-key-0123456789");
        builder.UseSetting("RabbitMq:Host", "");   // no consumer: the tests call the handler directly

        builder.ConfigureTestServices(services =>
        {
            // No background workers: CommandTimeoutSweeper sweeps at start-up on the SAME in-memory SQLite connection the
            // test is using, and a SqliteConnection is not thread-safe (it made 1 test in ~3 fail at random). The timeout
            // use case has its own tests in ms-valve's Application tests.
            services.RemoveAll<IHostedService>();

            services.RemoveAll<ValveDbContext>();
            services.RemoveAll<DbContextOptions<ValveDbContext>>();
            var options = new DbContextOptionsBuilder<ValveDbContext>().UseSqlite(_sqlite).Options;
            services.AddScoped<ValveDbContext>(_ => new SqliteValveDbContext(options));

            services.RemoveAll<IPlaceAccessChecker>();
            services.AddSingleton<IPlaceAccessChecker>(Places);
            services.RemoveAll<IConfirmationCodeVerifier>();
            services.AddSingleton<IConfirmationCodeVerifier, FakeCodeVerifier>();
            services.RemoveAll<IDeviceGateway>();
            services.AddSingleton<IDeviceGateway>(Devices);
            services.RemoveAll<ITokenRevocationChecker>();
            services.AddSingleton<ITokenRevocationChecker, NoRevocations>();
        });
    }

    public void CreateSchema()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ValveDbContext>().Database.EnsureCreated();
    }

    public string TokenFor(Guid userId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["jti"] = Guid.NewGuid().ToString(),
                ["name"] = "Juan Ome",
            },
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "iam-key-1" }, SecurityAlgorithms.RsaSha256),
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _sqlite.Dispose();
        File.Delete(_publicKeyPath);
    }
}
