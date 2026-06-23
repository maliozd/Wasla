using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Admin;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.UnitTests.Admin;

public sealed class CliProvisionSignupRequestTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeProvisioningService _provisioning = new();
    private readonly IHost _host;

    public CliProvisionSignupRequestTests()
    {
        _connection.Open();
        _host = new HostBuilder()
            .ConfigureServices(services =>
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CustomerDb:ServerInstance"] = "test-sql"
                    })
                    .Build();

                services.AddSingleton<IConfiguration>(config);
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_connection));
                services.AddScoped<IPendingRegistrationProvisioningService>(_ => _provisioning);
            })
            .Build();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _host.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProvisionSignupRequestAsync_DryRun_DoesNotCallProvisioningServiceOrMutate()
    {
        var reg = await SeedRegistrationAsync("dryrunslug");

        var exitCode = await WithConsoleCaptureAsync(() => CliCommands.ProvisionSignupRequestAsync(
            _host,
            reg.Id,
            dryRun: true,
            force: true,
            sqlServer: null,
            sqlAuth: "sql:user:secret",
            ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, exitCode);
        Assert.Equal(0, _provisioning.CallCount);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        Assert.Equal(0, await db.Tenants.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, (await db.PendingRegistrations.SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(ProvisioningOutcome.Success, 0)]
    [InlineData(ProvisioningOutcome.AlreadyProvisioned, 0)]
    [InlineData(ProvisioningOutcome.NotFound, 2)]
    [InlineData(ProvisioningOutcome.NotEligible, 2)]
    [InlineData(ProvisioningOutcome.ValidationError, 2)]
    [InlineData(ProvisioningOutcome.Failed, 3)]
    public async Task ProvisionSignupRequestAsync_MapsStructuredOutcomesToCliExitCodes(
        ProvisioningOutcome outcome,
        int expectedExitCode)
    {
        var reg = await SeedRegistrationAsync($"outcome{(int)outcome}");
        _provisioning.Result = BuildResult(outcome);

        var exitCode = await WithConsoleCaptureAsync(() => CliCommands.ProvisionSignupRequestAsync(
            _host,
            reg.Id,
            dryRun: false,
            force: true,
            sqlServer: "custom-sql",
            sqlAuth: "trusted",
            ct: TestContext.Current.CancellationToken));

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, _provisioning.CallCount);
        Assert.True(_provisioning.LastForce);
        Assert.Equal("custom-sql", _provisioning.LastSqlServerOverride);
        Assert.Null(_provisioning.LastSqlAuthOverride);
    }

    private async Task<PendingRegistration> SeedRegistrationAsync(string slug)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var reg = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = PendingRegistrationStatus.PaymentSucceeded,
            Slug = slug,
            BusinessName = $"Business {slug}",
            PrimaryDomain = $"{slug}.wasla.local",
            DatabaseName = $"Wasla_{slug}",
            BusinessPhone = "+905551112233",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            OwnerFullName = "Owner",
            OwnerEmail = $"{slug}@test.local",
            PasswordHash = "hash",
            PlanCode = "starter",
            BillingPeriod = "monthly",
            CreatedAtUtc = DateTime.UtcNow,
            PaymentSucceededAtUtc = DateTime.UtcNow
        };
        db.PendingRegistrations.Add(reg);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return reg;
    }

    private static ProvisioningResult BuildResult(ProvisioningOutcome outcome) => outcome switch
    {
        ProvisioningOutcome.Success => ProvisioningResult.Success(Guid.NewGuid(), "Tenant", "tenant.wasla.local"),
        ProvisioningOutcome.AlreadyProvisioned => ProvisioningResult.AlreadyProvisioned(Guid.NewGuid(), "Tenant", "tenant.wasla.local"),
        ProvisioningOutcome.NotFound => ProvisioningResult.NotFound(),
        ProvisioningOutcome.NotEligible => ProvisioningResult.NotEligible("not eligible"),
        ProvisioningOutcome.ValidationError => ProvisioningResult.ValidationError("validation failed"),
        _ => ProvisioningResult.Failure("safe failure")
    };

    private static async Task<int> WithConsoleCaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            return await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class FakeProvisioningService : IPendingRegistrationProvisioningService
    {
        public ProvisioningResult Result { get; set; } =
            ProvisioningResult.Success(Guid.NewGuid(), "Tenant", "tenant.wasla.local");

        public int CallCount { get; private set; }
        public bool? LastForce { get; private set; }
        public string? LastSqlServerOverride { get; private set; }
        public string? LastSqlAuthOverride { get; private set; }

        public Task<ProvisioningResult> ProvisionAsync(
            Guid registrationId,
            bool force = false,
            string? sqlServerOverride = null,
            string? sqlAuthOverride = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastForce = force;
            LastSqlServerOverride = sqlServerOverride;
            LastSqlAuthOverride = sqlAuthOverride;
            return Task.FromResult(Result);
        }
    }
}
