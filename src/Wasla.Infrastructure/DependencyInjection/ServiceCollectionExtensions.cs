using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FluentValidation;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Branches;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.Notifications;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Onboarding;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
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

        services.Configure<TrendyolGoOptions>(configuration.GetSection(TrendyolGoOptions.SectionName));
        services.Configure<YemeksepetiOptions>(configuration.GetSection(YemeksepetiOptions.SectionName));
        services.Configure<CustomerOnboardingOptions>(configuration.GetSection(CustomerOnboardingOptions.SectionName));

        services.AddWaslaEmail(configuration);

        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddSingleton<ISignupCompletionTokenService, SignupCompletionTokenService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();
        services.AddScoped<ISignupReferenceDataService, SignupReferenceDataService>();

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
            });
            services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();

            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();

            services.AddHttpClient<IFoodPlatformClient, TrendyolGoFoodPlatformClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<TrendyolGoOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = opts.RequestTimeout;
            });
        }

        services.AddScoped<ITenantResolver, TenantResolver>();

        services.AddScoped<IAuthValidationService, AuthValidationService>();
        services.AddScoped<ICentralAdminAuthService, CentralAdminAuthService>();
        services.AddScoped<ICentralAdminTenantService, CentralAdminTenantService>();
        services.AddScoped<IDashboardService, DashboardService>();
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
        services.AddScoped<IPrintBridgeDeviceManagementService, PrintBridgeDeviceManagementService>();
        services.AddScoped<IPrintBridgeSetupSessionService, PrintBridgeSetupSessionService>();
        services.AddScoped<IPrintJobHistoryService, PrintJobHistoryService>();

        return services;
    }
}

