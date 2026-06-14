using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FluentValidation;
using OrderHub.Application.Abstractions.Admin;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Application.Abstractions.Branches;
using OrderHub.Application.Abstractions.Dashboard;
using OrderHub.Application.Abstractions.Notifications;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Onboarding;
using OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Options;
using OrderHub.Infrastructure.Plans;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Platform;
using OrderHub.Infrastructure.Platform.Mock;
using OrderHub.Infrastructure.Platform.Mapping;
using OrderHub.Infrastructure.Platform.TrendyolGo;
using OrderHub.Infrastructure.Platform.Yemeksepeti;
using OrderHub.Infrastructure.Security;
using OrderHub.Infrastructure.Services;
using OrderHub.Infrastructure.Tenant;

namespace OrderHub.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatorsFromAssembly(typeof(OrderHub.Application.Branches.CreateBranchCommandValidator).Assembly);

        services.AddDbContext<CentralDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("CentralDb")));

        services.AddSingleton<ISecretManager, AesSecretManager>();
        services.AddSingleton<ICustomerDbContextFactory, CustomerDbContextFactory>();
        services.AddSingleton<IOrderStatusMapper, DefaultOrderStatusMapper>();

        services.Configure<TrendyolGoOptions>(configuration.GetSection(TrendyolGoOptions.SectionName));
        services.Configure<YemeksepetiOptions>(configuration.GetSection(YemeksepetiOptions.SectionName));
        services.Configure<CustomerOnboardingOptions>(configuration.GetSection(CustomerOnboardingOptions.SectionName));

        services.AddSingleton<IOrderHubPlanCatalog, OrderHubPlanCatalog>();
        services.AddSingleton<ISignupCompletionTokenService, SignupCompletionTokenService>();
        services.AddScoped<ICustomerOnboardingService, CustomerOnboardingService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();

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

        services.AddScoped<ICustomerResolver, CustomerResolver>();

        services.AddScoped<IAuthValidationService, AuthValidationService>();
        services.AddScoped<ICentralAdminAuthService, CentralAdminAuthService>();
        services.AddScoped<ICentralAdminCustomerService, CentralAdminCustomerService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IOrderReadService, OrderReadService>();
        services.AddScoped<IOrderActionService, OrderActionService>();
        services.AddScoped<IOrderSyncSettingsService, OrderSyncSettingsService>();
        services.AddScoped<ICustomerOrderSettingsService, CustomerOrderSettingsService>();
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
        services.AddScoped<IPrintJobHistoryService, PrintJobHistoryService>();

        return services;
    }
}

