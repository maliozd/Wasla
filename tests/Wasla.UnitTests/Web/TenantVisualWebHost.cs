using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.Web;
using Wasla.Web.GuidedSetup;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;
using CurrentTenantService = Wasla.Web.Tenant.CurrentTenantService;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Serves the populated tenant application for visual checks: Dashboard, Orders and an order's details, the Live Screen
/// with its snapshot, Platform Connections, Users and the tenant settings pages, all through the real controllers, compiled
/// Razor views, wwwroot and Infrastructure services. CentralDb and the tenant database are file-backed SQLite databases
/// seeded with fake orders, connections, users and devices; nothing reaches a shared database, a provider or a printer.
/// Tenant resolution is replaced by one fixed tenant, and a test-only endpoint signs in its Owner with no password.
/// </summary>
internal sealed class TenantVisualWebHost : IAsyncDisposable
{
    public const string SignInPath = "/__test/tenant-sign-in";

    public static readonly Guid TenantId = Guid.Parse("81d0a2b4-3c55-4f0e-9a81-0d8a2c6e7b81");
    public static readonly Guid OwnerId = Guid.Parse("81000000-0000-4000-8000-000000000001");
    public static readonly Guid NewOwnerId = Guid.Parse("81000000-0000-4000-8000-000000000002");
    public static readonly Guid ResumingOwnerId = Guid.Parse("81000000-0000-4000-8000-000000000003");
    public static readonly Guid DetailOrderId = Guid.Parse("81000000-0000-4000-8000-0000000000a1");

    private readonly WebApplication _app;
    private readonly string _contentRoot;
    private readonly CentralTestDatabase _central;
    private readonly RecordingTenantDbFactory _tenants;

    private TenantVisualWebHost(WebApplication app, string contentRoot, CentralTestDatabase central, RecordingTenantDbFactory tenants)
    {
        _app = app;
        _contentRoot = contentRoot;
        _central = central;
        _tenants = tenants;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    /// <summary>
    /// <paramref name="tenantInSetup"/> seeds a tenant still in Setup with its setup checklist open, and three Owners at
    /// different points of guided setup: <see cref="OwnerId"/> in order training, <see cref="NewOwnerId"/> before the
    /// first-use decision and <see cref="ResumingOwnerId"/> in the platform connections section.
    /// </summary>
    public static Task<TenantVisualWebHost> StartAsync(bool tenantInSetup = false) =>
        StartAsync(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot"), "http://127.0.0.1:0", tenantInSetup);

    public static Task<TenantVisualWebHost> StartAsync(string webRootPath, string url) => StartAsync(webRootPath, url, tenantInSetup: false);

    public static async Task<TenantVisualWebHost> StartAsync(string webRootPath, string url, bool tenantInSetup)
    {
        var central = new CentralTestDatabase();
        var tenants = new RecordingTenantDbFactory();
        SeedCentral(central);
        tenants.CreateTenantDatabase(TenantId);
        tenants.Override(TenantId, _ => Task.FromResult<TenantDbContext>(new SqliteAggregateTenantDbContext(tenants.ConnectionStringFor(TenantId))));
        using (var seed = new SqliteAggregateTenantDbContext(tenants.ConnectionStringFor(TenantId)))
        {
            SeedTenant(seed, DateTime.UtcNow, tenantInSetup);
            seed.SaveChanges();
        }

        var contentRoot = Directory.CreateTempSubdirectory("wasla-visual-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(SharedResource).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            // The real stylesheets and scripts, so a real browser renders the pages as deployed.
            WebRootPath = webRootPath
        });
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platforms:ProviderMode"] = "Mock",
            // Never opened: CentralDbContext is replaced by SQLite below.
            ["ConnectionStrings:CentralDb"] = "Server=(none);Database=WaslaVisualTests"
        });

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var supported = new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" }.Select(CultureInfo.GetCultureInfo).ToList();
            options.DefaultRequestCulture = new RequestCulture("tr-TR");
            options.SupportedCultures = supported;
            options.SupportedUICultures = supported;
            options.RequestCultureProviders = new List<IRequestCultureProvider> { new CookieRequestCultureProvider() };
        });
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = AuthSchemes.Tenant;
                options.DefaultAuthenticateScheme = AuthSchemes.Tenant;
                options.DefaultChallengeScheme = AuthSchemes.Tenant;
            })
            .AddCookie(AuthSchemes.Tenant, options =>
            {
                options.Cookie.Name = TenantAuthCookieNames.Active;
                options.LoginPath = "/auth/login";
                options.AccessDeniedPath = "/auth/access-denied";
            })
            .AddCookie(AuthSchemes.CentralAdmin, options => options.Cookie.Name = CentralAdminAuthCookieNames.Active);

        // Same tenant role policies as Program.cs.
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        services.AddScoped<ITenantNavigationAuthorizationService, TenantNavigationAuthorizationService>();
        services.AddScoped<IGuidedSetupCoordinator, GuidedSetupCoordinator>();
        services.AddAuthorization(options =>
        {
            AddTenantRolePolicy(options, TenantPolicies.TenantOwner, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
            AddTenantRolePolicy(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManageTenantSettings, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddTenantRolePolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddTenantRolePolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            AddTenantRolePolicy(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddTenantRolePolicy(options, TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
        });
        services.AddControllersWithViews()
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

        // The real application services, with both databases replaced by the seeded SQLite ones.
        services.AddWaslaInfrastructure(builder.Configuration);
        services.RemoveAll<DbContextOptions<CentralDbContext>>();
        services.RemoveAll<CentralDbContext>();
        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(central.ConnectionString));
        services.RemoveAll<ITenantDbContextFactory>();
        services.AddSingleton<ITenantDbContextFactory>(tenants);

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseRequestLocalization();
        app.UseRouting();

        // Stands in for TenantResolutionMiddleware: every request is this tenant.
        var tenant = new ResolvedTenantDto(TenantId, "Visual Test Restaurant", "visual-test", "127.0.0.1");
        app.Use((http, next) =>
        {
            http.Items["CurrentTenant"] = tenant;
            return next(http);
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(SignInPath, async (HttpContext http) =>
        {
            // ?as=new or ?as=resume signs in one of the Owners seeded for a tenant in setup.
            var userId = http.Request.Query["as"].ToString() switch
            {
                "new" => NewOwnerId,
                "resume" => ResumingOwnerId,
                _ => OwnerId
            };
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, "Visual Owner"),
                    new Claim(ClaimTypes.Email, "owner@visual.test"),
                    new Claim("TenantId", TenantId.ToString()),
                    new Claim(ClaimTypes.Role, nameof(UserRole.Owner))
                ],
                AuthSchemes.Tenant);
            await http.SignInAsync(AuthSchemes.Tenant, new ClaimsPrincipal(identity));
            return Results.Ok();
        });

        app.MapControllers();
        app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

        await app.StartAsync();
        return new TenantVisualWebHost(app, contentRoot, central, tenants);
    }

    /// <summary>
    /// A real browser with the culture cookie for this origin and a signed-in Owner session, at the given viewport. Null
    /// when no Chromium browser is installed.
    /// </summary>
    public async Task<HeadlessChromium?> BrowserAsync(string culture, int width, int height, bool mobile, CancellationToken ct, string? signInAs = null)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            return null;

        var cookies = new System.Net.CookieContainer();
        using (var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = BaseAddress })
            (await client.GetAsync(signInAs is null ? SignInPath : $"{SignInPath}?as={signInAs}", ct)).EnsureSuccessStatusCode();

        var browser = await HeadlessChromium.StartAsync(executable, ct);
        foreach (System.Net.Cookie cookie in cookies.GetCookies(BaseAddress))
            await browser.SetCookieAsync(BaseAddress, cookie.Name, cookie.Value, ct);
        await browser.SetCookieAsync(BaseAddress, CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
        await browser.SetViewportAsync(width, height, mobile, ct);
        return browser;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    private static void AddTenantRolePolicy(AuthorizationOptions options, string name, params UserRole[] roles) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(roles));
        });

    private static void SeedCentral(CentralTestDatabase central)
    {
        using var db = central.CreateContext();
        var now = DateTime.UtcNow;
        db.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Visual Test Restaurant",
            Slug = "visual-test",
            PrimaryDomain = "visual-test.wasla.local",
            DatabaseName = "Wasla_Tenant_visual_test",
            EncryptedConnectionString = SecretMarkers.EncryptedConnectionString,
            IsActive = true,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-30)
        });
        db.PrintBridgeDevices.Add(new PrintBridgeDevice
        {
            TenantId = TenantId,
            Name = "Kitchen printer",
            TokenHash = SecretMarkers.TokenHash + "kitchen",
            IsActive = true,
            LastSeenAt = now.AddSeconds(-20),
            AppVersion = "1.4.2",
            MachineName = "KITCHEN-PC",
            InstallationId = Guid.NewGuid()
        });
        db.PrintBridgeDevices.Add(new PrintBridgeDevice
        {
            TenantId = TenantId,
            Name = "Front desk",
            TokenHash = SecretMarkers.TokenHash + "desk",
            IsActive = true,
            LastSeenAt = now.AddDays(-2),
            AppVersion = "1.4.0",
            MachineName = "DESK-PC",
            InstallationId = Guid.NewGuid()
        });
        db.SaveChanges();
    }

    private static void SeedTenant(TenantDbContext db, DateTime now, bool tenantInSetup)
    {
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = TenantOperationalModes.SettingsId,
            OperationalMode = tenantInSetup ? TenantOperationalMode.Setup : TenantOperationalMode.Live,
            OrderSyncEnabled = true,
            AutoApproveNewOrders = false,
            AutoPrintReceiptOnAutoApprove = true,
            SetupGuidanceCompletedAtUtc = tenantInSetup ? null : now.AddDays(-20)
        });

        var branch = new Branch { Name = "Kadıköy", Address = "Moda Cd. 12, İstanbul", IsActive = true };
        db.Branches.Add(branch);

        db.AppUsers.Add(new AppUser { Id = OwnerId, Email = "owner@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Ayşe Owner", Role = UserRole.Owner, IsActive = true, LastLoginAt = now.AddMinutes(-5), CreatedAt = now.AddDays(-30) });
        db.AppUsers.Add(new AppUser { Email = "manager@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Selim Manager", Role = UserRole.Manager, IsActive = true, LastLoginAt = now.AddDays(-1), CreatedAt = now.AddDays(-25) });
        db.AppUsers.Add(new AppUser { Email = "kitchen@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Kitchen Screen", Role = UserRole.Kitchen, IsActive = false, CreatedAt = now.AddDays(-20) });
        db.AppUsers.Add(new AppUser { Email = "viewer@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "", Role = UserRole.Viewer, IsActive = true, CreatedAt = now.AddDays(-2) });
        if (tenantInSetup)
        {
            db.AppUsers.Add(new AppUser { Id = NewOwnerId, Email = "new-owner@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "New Owner", Role = UserRole.Owner, IsActive = true, CreatedAt = now.AddDays(-1) });
            db.AppUsers.Add(new AppUser { Id = ResumingOwnerId, Email = "resuming-owner@visual.test", PasswordHash = SecretMarkers.TenantUserPasswordHash, FullName = "Resuming Owner", Role = UserRole.Owner, IsActive = true, CreatedAt = now.AddDays(-1) });
            db.UserGuidedSetupStates.Add(new UserGuidedSetupState { UserId = OwnerId, Status = GuidedSetupStatus.InProgress, CurrentSectionKey = GuidedSetupSections.LiveScreenDemo, StartedAtUtc = now.AddHours(-1) });
            db.UserGuidedSetupStates.Add(new UserGuidedSetupState { UserId = NewOwnerId, Status = GuidedSetupStatus.NotStarted });
            db.UserGuidedSetupStates.Add(new UserGuidedSetupState { UserId = ResumingOwnerId, Status = GuidedSetupStatus.InProgress, CurrentSectionKey = GuidedSetupSections.PlatformConnections, StartedAtUtc = now.AddHours(-1) });
        }
        else
        {
            db.UserGuidedSetupStates.Add(new UserGuidedSetupState { UserId = OwnerId, Status = GuidedSetupStatus.Completed, StartedAtUtc = now.AddDays(-20), CompletedAtUtc = now.AddDays(-19) });
        }

        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.Yemeksepeti, StoreId = "YS-1001", EncryptedApiKey = SecretMarkers.ApiKey,
            EncryptedApiSecret = SecretMarkers.ApiSecret, IsActive = true, LastSyncAttempt = now.AddSeconds(-30), LastSuccessfulSync = now.AddSeconds(-30)
        });
        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek, StoreId = "TY-2002", SupplierId = "5501", EncryptedApiKey = SecretMarkers.ApiKey,
            EncryptedApiSecret = SecretMarkers.ApiSecret, IsActive = true, LastSyncAttempt = now.AddMinutes(-1), LastSuccessfulSync = now.AddHours(-3),
            ConsecutiveFailures = 4, CircuitOpenUntil = now.AddMinutes(10)
        });

        var orders = new[]
        {
            Order(DetailOrderId, FoodPlatform.Yemeksepeti, "YS-48211", OrderStatus.New, "Zeynep Kaya", 412.50m, now.AddMinutes(-2), "Please ring the bell twice."),
            Order(Guid.NewGuid(), FoodPlatform.TrendyolYemek, "TY-77310", OrderStatus.Accepted, "Can Demir", 238.00m, now.AddMinutes(-6), null),
            Order(Guid.NewGuid(), FoodPlatform.GetirYemek, "GY-10944", OrderStatus.Preparing, "Elif Şahin", 189.90m, now.AddMinutes(-11), null),
            Order(Guid.NewGuid(), FoodPlatform.Yemeksepeti, "YS-48190", OrderStatus.ReadyForPickup, "Burak Yılmaz", 655.00m, now.AddMinutes(-18), "No cutlery."),
            Order(Guid.NewGuid(), FoodPlatform.TrendyolYemek, "TY-77288", OrderStatus.OnTheWay, "Selin Arslan", 302.40m, now.AddMinutes(-31), null),
            Order(Guid.NewGuid(), FoodPlatform.GetirYemek, "GY-10901", OrderStatus.Delivered, "Deniz Aydın", 147.00m, now.AddMinutes(-45), null, deliveredAt: now.AddMinutes(-1)),
            Order(Guid.NewGuid(), FoodPlatform.Yemeksepeti, "YS-48102", OrderStatus.Cancelled, "Okan Çelik", 96.50m, now.AddMinutes(-70), null),
            Order(Guid.NewGuid(), FoodPlatform.TrendyolYemek, "TY-76002", OrderStatus.Delivered, "Ece Koç", 274.00m, now.AddDays(-1), null, deliveredAt: now.AddDays(-1).AddMinutes(30))
        };
        db.Orders.AddRange(orders);

        db.PrintJobs.Add(new PrintJob { OrderId = orders[1].Id, Type = PrintJobType.Receipt, Status = PrintJobStatus.Printed, PayloadJson = "{}", AttemptCount = 1, PrintedAt = now.AddMinutes(-5) });
        db.PrintJobs.Add(new PrintJob { OrderId = orders[2].Id, Type = PrintJobType.Receipt, Status = PrintJobStatus.Failed, PayloadJson = "{}", AttemptCount = 3, ErrorMessage = "Printer offline", LastAttemptAt = now.AddMinutes(-9) });
        db.PrintJobs.Add(new PrintJob { OrderId = orders[3].Id, Type = PrintJobType.Receipt, Status = PrintJobStatus.Pending, PayloadJson = "{}" });
    }

    private static Order Order(Guid id, FoodPlatform platform, string code, OrderStatus status, string customer, decimal total,
        DateTime receivedAt, string? note, DateTime? deliveredAt = null) => new()
    {
        Id = id,
        Platform = platform,
        ExternalOrderId = code,
        ExternalOrderCode = code,
        IdempotencyKey = $"{platform}:{code}",
        InternalStatus = status,
        PlatformStatus = status.ToString(),
        CustomerName = customer,
        CustomerPhone = "5551112233",
        CustomerAddress = "Caferağa Mh. Moda Cd. No: 12, Kadıköy",
        CustomerNote = note,
        TotalAmount = total,
        DeliveryFee = 19.90m,
        PaymentMethod = PaymentMethod.OnlinePayment,
        PaymentStatus = PaymentStatus.Paid,
        CreatedAtPlatform = receivedAt,
        ReceivedAt = receivedAt,
        AcceptedAt = status is OrderStatus.New or OrderStatus.Cancelled ? null : receivedAt.AddMinutes(1),
        DeliveredAt = deliveredAt,
        CancelledAt = status == OrderStatus.Cancelled ? receivedAt.AddMinutes(4) : null,
        RawPayloadJson = "{}",
        Items =
        [
            new OrderItem { ProductName = "Adana Kebap", Quantity = 2, UnitPrice = 140m, TotalPrice = 280m, Notes = "Extra spicy" },
            new OrderItem { ProductName = "Ayran", Quantity = 2, UnitPrice = 25m, TotalPrice = 50m },
            new OrderItem { ProductName = "Künefe", Quantity = 1, UnitPrice = 82.5m, TotalPrice = 82.5m }
        ]
    };

    /// <summary>
    /// SQLite cannot sum decimals, which the Dashboard does on SQL Server, so amounts are read as doubles here. Test data
    /// only: the product keeps its decimal columns.
    /// </summary>
    private sealed class SqliteAggregateTenantDbContext(string connectionString)
        : TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).Options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
        }
    }
}
