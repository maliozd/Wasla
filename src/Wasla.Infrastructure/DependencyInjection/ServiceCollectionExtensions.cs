using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using FluentValidation;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Branches;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.DevelopmentTools;
using Wasla.Application.Abstractions.Notifications;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Integrations.Platform;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Abstractions.Reports;
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
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Tenant;
using Wasla.Integrations.DependencyInjection;

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

        // The webhook pilot's provider API override routes its one connection; every host that calls Trendyol GO binds it,
        // and the Trendyol GO client asks it before every request (ITrendyolGoApiRouter, WAS-233).
        services.AddTrendyolGoMealWebhookPilotOptions(configuration);
        services.AddOptions<TenantOnboardingOptions>()
            .Bind(configuration.GetSection(TenantOnboardingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TenantOnboardingOptions>, TenantOnboardingOptionsValidator>());

        services.AddWaslaEmail(configuration);

        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddSingleton<ISignupCompletionTokenService, SignupCompletionTokenService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();
        services.AddScoped<IBillingSubscriptionStore, BillingSubscriptionStore>();
        services.AddScoped<IBillingPlanPriceAdminService, BillingPlanPriceAdminService>();
        services.AddScoped<IBillingPlanPriceReader, BillingPlanPriceReader>();
        services.AddScoped<ISignupReferenceDataService, SignupReferenceDataService>();
        services.AddScoped<ITenantBusinessSubtypeReader, TenantBusinessSubtypeReader>();

        // The food-platform provider clients, Mock or Real by Platforms:ProviderMode, with their options and provider HTTP
        // clients: the provider library's own registration (WAS-233).
        services.AddWaslaIntegrations(configuration);

        services.AddScoped<ITenantResolver, TenantResolver>();

        services.AddScoped<IAuthValidationService, AuthValidationService>();
        services.AddScoped<ITenantLoginRecorder, TenantLoginRecorder>();
        services.AddScoped<ITenantSessionValidator, TenantSessionValidator>();
        services.AddScoped<ITenantSessionRegistry, TenantSessionRegistry>();
        services.AddScoped<IPasswordPolicy, DefaultPasswordPolicy>();
        services.AddScoped<ITenantPasswordResetService, TenantPasswordResetService>();
        services.AddScoped<ITenantUserRoleService, TenantUserRoleService>();
        services.AddScoped<ICentralAdminAuthService, CentralAdminAuthService>();
        services.AddScoped<ICentralAdminSessionValidator, CentralAdminSessionValidator>();
        services.AddScoped<ICentralAdminTenantService, CentralAdminTenantService>();
        services.AddScoped<ICentralAdminTenantOperationsService, CentralAdminTenantOperationsService>();
        services.AddOptions<TenantOperationalHealthOptions>();
        // Singleton so concurrent detail requests for the same tenant share one in-flight read.
        services.AddSingleton<ITenantOperationalHealthReader, TenantOperationalHealthReader>();
        services.AddScoped<ICentralAdminPendingRegistrationService, CentralAdminPendingRegistrationService>();
        services.AddScoped<ITenantDatabaseProvisioningOperations, SqlServerTenantDatabaseProvisioningOperations>();
        services.AddScoped<IPendingRegistrationProvisioningService, PendingRegistrationProvisioningService>();
        services.AddScoped<IOrderReportService, OrderReportService>();
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
        services.AddScoped<IRestaurantLogoService, RestaurantLogoService>();
        services.AddScoped<IOrderAutoApproveService, OrderAutoApproveService>();
        services.AddScoped<IOrderReceiptCreationService, OrderReceiptCreationService>();
        services.AddScoped<IReceiptPrintJobService, ReceiptPrintJobService>();
        services.AddScoped<IPlatformConnectionService, PlatformConnectionService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<IUserNotificationSettingsService, UserNotificationSettingsService>();
        services.AddScoped<IUserOrderActionPreferenceService, UserOrderActionPreferenceService>();
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
        // Live change notifications: dropped unless the host registers a channel (Wasla.Web's SignalR hub, or the
        // internal channel to Wasla.Web in Wasla.Worker and Wasla.Api).
        services.TryAddSingleton<ITenantChangeNotifier>(NullTenantChangeNotifier.Instance);

        return services;
    }
}

