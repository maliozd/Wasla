using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Abstractions.Sms;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sms;

namespace Wasla.Infrastructure.DependencyInjection;

public static class RegistrationDraftServiceCollectionExtensions
{
    /// <summary>
    /// Registration drafts and owner phone verification (WAS-192, Wasla.Web only): <c>Wasla:Signup</c> (validated at
    /// start), the SMS sender of <c>Wasla:Sms:Mode</c>, the draft and verification services, and the cleanup service.
    /// Needs CentralDb, the signup reference data, the plan catalog, Data Protection and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddWaslaRegistrationDrafts(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<SignupOptions>()
            .Bind(configuration.GetSection(SignupOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SignupOptions>, SignupOptionsValidator>());

        services.AddWaslaSms(configuration, environment);
        services.TryAddSingleton<SmsMessageTemplates>();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<PendingRegistrationService>();
        services.TryAddScoped<IRegistrationDraftService, RegistrationDraftService>();
        services.TryAddScoped<IRegistrationPhoneVerificationService, RegistrationPhoneVerificationService>();
        services.TryAddScoped<RegistrationDraftCleanup>();
        services.AddHostedService<RegistrationDraftCleanupService>();
        return services;
    }

    /// <summary>
    /// The SMS sender of <c>Wasla:Sms:Mode</c>. Missing, empty or <c>Disabled</c>: <see cref="DisabledSmsSender"/>, so
    /// verification fails closed. <c>Fake</c>: the Development outbox; any other environment refuses to start. Any other
    /// value refuses to start. The real provider mode is WAS-193.
    /// </summary>
    public static IServiceCollection AddWaslaSms(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var mode = configuration.GetSection(SmsOptions.SectionName)[nameof(SmsOptions.Mode)]?.Trim();
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, SmsModes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<ISmsSender, DisabledSmsSender>();
            return services;
        }

        if (string.Equals(mode, SmsModes.Fake, StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{SmsOptions.SectionName}:{nameof(SmsOptions.Mode)} '{SmsModes.Fake}' is allowed only in Development; " +
                    $"environment '{environment.EnvironmentName}' needs a real SMS sender or '{SmsModes.Disabled}'.");
            }

            services.TryAddSingleton<FakeSmsOutbox>();
            services.TryAddSingleton<ISmsSender, FakeSmsSender>();
            return services;
        }

        throw new InvalidOperationException(
            $"Unknown {SmsOptions.SectionName}:{nameof(SmsOptions.Mode)} '{mode}'. Supported values are '{SmsModes.Disabled}' and '{SmsModes.Fake}' (Development only).");
    }
}
