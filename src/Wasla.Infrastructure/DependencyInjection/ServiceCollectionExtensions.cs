using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FluentValidation;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Branches;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.DevelopmentTools;
using Wasla.Application.Abstractions.Notifications;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Onboarding;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Abstractions.Tours;
using Wasla.Application.Demos;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.Mock;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Tenant;

namespace Wasla.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWaslaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatorsFromAssembly(typeof(Wasla.Application.Branches.CreateBranchCommandValidator).Assembly);

        services.AddDbContext<CentralDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("CentralDb")));

        services.AddSingleton<ISecretManager, AesSecretManager>();
        services.AddSingleton<ITenantDbContextFactory, TenantDbContextFactory>();
        services.AddSingleton<IOrderStatusMapper, DefaultOrderStatusMapper>();
        services.AddSingleton(TimeProvider.System);

        services.Configure<TrendyolGoOptions>(configuration.GetSection(TrendyolGoOptions.SectionName));
        services.Configure<YemeksepetiOptions>(configuration.GetSection(YemeksepetiOptions.SectionName));
        services.Configure<CustomerOnboardingOptions>(configuration.GetSection(CustomerOnboardingOptions.SectionName));

        services.AddWaslaEmail(configuration);

        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddSingleton<ISignupCompletionTokenService, SignupCompletionTokenService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();
        services.AddScoped<ISignupReferenceDataService, SignupReferenceDataService>();
        services.AddScoped<ITenantBusinessSubtypeReader, TenantBusinessSubtypeReader>();

        var providerMode = ProviderModeResolver.Resolve(configuration);

        if (providerMode.IsMock)
        {
            // Mock mode: all three platforms use mock clients. No real provider HTTP calls.
            services.AddSingleton<IFoodPlatformClient, MockYemeksepetiFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient, MockTrendyolYemekFoodPlatformClient>();
        }
        else
        {
            // Real mode:
            //   Yemeksepeti → real YemeksepetiFoodPlatformClient (OAuth2 + Partner Picking API)
            //   GetirYemek  → still mock (real client not yet implemented)
            //   TrendyolYemek → real TrendyolGoFoodPlatformClient (Basic auth + Trendyol GO API)
            services.AddHttpClient(YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName, (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<YemeksepetiOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = opts.RequestTimeout;
            }).ConfigureProviderPrimaryHandler();
            services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();

            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();

            // One limiter per process: every Trendyol GO client instance (typed clients are transient) and every
            // tenant share its request budget.
            services.AddSingleton<TrendyolRequestRateLimiter>();
            services.AddHttpClient<IFoodPlatformClient, TrendyolGoFoodPlatformClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<TrendyolGoOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = opts.RequestTimeout;
            }).ConfigureProviderPrimaryHandler();
        }

        services.AddScoped<ITenantResolver, TenantResolver>();

        services.AddScoped<IAuthValidationService, AuthValidationService>();
        services.AddScoped<ITenantLoginRecorder, TenantLoginRecorder>();
        services.AddScoped<ITenantSessionValidator, TenantSessionValidator>();
        services.AddScoped<IPasswordPolicy, DefaultPasswordPolicy>();
        services.AddScoped<ITenantPasswordResetService, TenantPasswordResetService>();
        services.AddScoped<ITenantUserRoleService, TenantUserRoleService>();
        services.AddScoped<ICentralAdminAuthService, CentralAdminAuthService>();
        services.AddScoped<ICentralAdminTenantService, CentralAdminTenantService>();
        services.AddScoped<ICentralAdminTenantOperationsService, CentralAdminTenantOperationsService>();
        services.AddOptions<TenantOperationalHealthOptions>();
        // Singleton so concurrent detail requests for the same tenant share one in-flight read.
        services.AddSingleton<ITenantOperationalHealthReader, TenantOperationalHealthReader>();
        services.AddScoped<ICentralAdminPendingRegistrationService, CentralAdminPendingRegistrationService>();
        services.AddScoped<ITenantDatabaseProvisioningOperations, SqlServerTenantDatabaseProvisioningOperations>();
        services.AddScoped<IPendingRegistrationProvisioningService, PendingRegistrationProvisioningService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ITenantSetupStatusService, TenantSetupStatusService>();
        services.AddScoped<IUserProductTourService, UserProductTourService>();
        services.AddScoped<IGuidedDemoService, GuidedDemoService>();
        services.AddScoped<IGuidedDemoDeliverySimulator, GuidedDemoDeliverySimulator>();
        services.AddScoped<IGuidedSetupService, GuidedSetupService>();
        services.AddScoped<ITenantOperationalModeService, TenantOperationalModeService>();
        services.AddScoped<IOrderReadService, OrderReadService>();
        services.AddScoped<IOrderActionService, OrderActionService>();
        services.AddScoped<IOrderSyncSettingsService, OrderSyncSettingsService>();
        services.AddScoped<ITenantOrderSettingsService, TenantOrderSettingsService>();
        services.AddScoped<IReceiptTemplateSettingsService, ReceiptTemplateSettingsService>();
        services.AddScoped<IOrderAutoApproveService, OrderAutoApproveService>();
        services.AddScoped<IOrderReceiptCreationService, OrderReceiptCreationService>();
        services.AddScoped<IReceiptPrintJobService, ReceiptPrintJobService>();
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IUserNotificationSettingsService, UserNotificationSettingsService>();
        services.AddScoped<IPrintBridgeAuthService, PrintBridgeAuthService>();
        services.AddScoped<IPrintBridgeJobService, PrintBridgeJobService>();
        services.AddScoped<IPrintBridgeActivePrintJobChecker, PrintBridgeActivePrintJobChecker>();
        services.AddScoped<IPrintBridgeDeviceManagementService, PrintBridgeDeviceManagementService>();
        services.AddScoped<IPrintBridgeSetupTenantLock, SqlServerPrintBridgeSetupTenantLock>();
        services.AddScoped<IPrintBridgeSetupSessionService, PrintBridgeSetupSessionService>();
        // Temporary Development tool; the Web layer only exposes it in Development with an explicit option.
        services.AddScoped<ITenantDevelopmentResetService, TenantDevelopmentResetService>();
        services.AddScoped<IPrintJobHistoryService, PrintJobHistoryService>();
        services.AddScoped<IManualOrderPrintService, ManualOrderPrintService>();

        return services;
    }

    /// <summary>
    /// Provider handlers are pooled by <see cref="IHttpClientFactory"/> and shared by every tenant (the Yemeksepeti
    /// client is a singleton), so they must hold no connection state. They never store or send cookies: a cookie set
    /// by one connection's response would otherwise go out with every later request to that host, whichever tenant
    /// sent it (WAS-95). Credentials are set on each request instead.
    /// They never follow redirects either: a followed redirect re-sends the request to whatever URL the response
    /// names, including the Yemeksepeti token request's client secret on a 307 or 308. A 3xx response reaches the
    /// client, which fails it like any other unsuccessful status.
    /// </summary>
    private static IHttpClientBuilder ConfigureProviderPrimaryHandler(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
        {
            if (handler is not HttpClientHandler primary)
            {
                throw new InvalidOperationException(
                    $"Provider HTTP clients expect an {nameof(HttpClientHandler)} primary handler, not {handler.GetType().Name}.");
            }

            primary.UseCookies = false;
            primary.AllowAutoRedirect = false;
        });
}

