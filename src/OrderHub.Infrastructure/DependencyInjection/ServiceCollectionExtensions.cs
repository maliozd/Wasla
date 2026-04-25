using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Application.Abstractions.Branches;
using OrderHub.Application.Abstractions.Dashboard;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.PlatformConnections;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Platform.Mapping;
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

        services.AddScoped<ICustomerResolver, CustomerResolver>();

        services.AddScoped<IAuthValidationService, AuthValidationService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IOrderReadService, OrderReadService>();
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddScoped<IBranchService, BranchService>();

        return services;
    }
}

