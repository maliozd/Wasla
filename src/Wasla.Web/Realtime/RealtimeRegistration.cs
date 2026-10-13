using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;

namespace Wasla.Web.Realtime;

/// <summary>Live tenant notifications in Wasla.Web: the hub, its notifier and the internal listener.</summary>
public static class RealtimeRegistration
{
    /// <summary>
    /// Registers SignalR, <see cref="SignalRTenantChangeNotifier"/> as this process's <see cref="ITenantChangeNotifier"/>
    /// and, when <c>Realtime:Internal</c> is configured, the loopback listener for Wasla.Worker and Wasla.Api. Call after
    /// AddWaslaInfrastructure. An invalid configuration stops startup.
    /// </summary>
    public static IServiceCollection AddWaslaRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR(options => options.EnableDetailedErrors = false);
        // The hub admits a user who may receive at least one stream; each stream's group is then joined only when its own
        // role check passes (TenantEventsHub.OnConnectedAsync).
        services.Configure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
            options.AddPolicy(
                TenantEventsHub.ConnectPolicy,
                TenantRolePolicyRegistration.BuildPolicy(TenantEventsHub.AnyStreamRoles, AuthSchemes.Tenant)));
        services.AddSingleton(RealtimeOptions.Read(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RealtimeSessionRevalidator>();
        services.RemoveAll<ITenantChangeNotifier>();
        services.AddSingleton<ITenantChangeNotifier, SignalRTenantChangeNotifier>();

        var internalOptions = InternalRealtimeOptions.Read(configuration);
        internalOptions.ThrowIfInvalid();
        if (internalOptions.IsEnabled)
        {
            services.AddSingleton(internalOptions);
            services.AddSingleton<InternalTenantChangeListener>();
            services.AddHostedService(sp => sp.GetRequiredService<InternalTenantChangeListener>());
        }

        return services;
    }

    /// <summary>The hub's same-origin check. Place it after routing and before tenant resolution and authentication.</summary>
    public static IApplicationBuilder UseWaslaRealtimeOriginCheck(this IApplicationBuilder app) =>
        app.UseMiddleware<RealtimeOriginMiddleware>();

    /// <summary>
    /// Maps <see cref="TenantEventsHub"/>. A connection closes when its cookie's authentication ticket expires; its
    /// session is also checked again periodically (<see cref="RealtimeSessionRevalidator"/>).
    /// </summary>
    public static IEndpointRouteBuilder MapWaslaRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<TenantEventsHub>(TenantEventsHub.Path, options =>
        {
            options.CloseOnAuthenticationExpiration = true;
            options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
        });
        return endpoints;
    }
}
