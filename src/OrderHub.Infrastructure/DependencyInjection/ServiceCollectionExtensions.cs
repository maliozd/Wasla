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
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Platform;
using OrderHub.Infrastructure.Platform.Mock;
using OrderHub.Infrastructure.Platform.Mapping;
using OrderHub.Infrastructure.Platform.TrendyolGo;
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

        var providerMode = ProviderModeResolver.Resolve(configuration);

        // Yemeksepeti and GetirYemek still use mock clients in both modes;
        // only TrendyolYemek switches to the real Trendyol GO HTTP client when ProviderMode=Real.
        services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();
        services.AddSingleton<IFoodPlatformClient, GetirYemekFoodPlatformClient>();

        if (providerMode.IsMock)
        {
            services.AddSingleton<IFoodPlatformClient, TrendyolYemekFoodPlatformClient>();
        }
        else
        {
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
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IUserNotificationSettingsService, UserNotificationSettingsService>();

        return services;
    }
}

