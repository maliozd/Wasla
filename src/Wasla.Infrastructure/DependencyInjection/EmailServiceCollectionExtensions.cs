using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Email;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
namespace Wasla.Infrastructure.DependencyInjection;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddWaslaEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddSingleton<EmailTemplateRenderer>();
        services.AddSingleton<ProvisioningEmailFactory>();
        services.AddSingleton<PasswordResetEmailFactory>();

        var provider = configuration.GetSection(EmailOptions.SectionName).GetValue<string>("Provider") ?? "Log";

        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else if (string.Equals(provider, "Log", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, LogEmailSender>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown Email:Provider '{provider}'. Supported values are 'Log' and 'Smtp'.");
        }

        return services;
    }
}