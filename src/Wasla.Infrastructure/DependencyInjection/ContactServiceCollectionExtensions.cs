using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Contact;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Services;

namespace Wasla.Infrastructure.DependencyInjection;

public static class ContactServiceCollectionExtensions
{
    /// <summary>
    /// The public contact form and its Central Admin list (Wasla.Web only). Needs CentralDb, the email services
    /// (<see cref="EmailServiceCollectionExtensions.AddWaslaEmail"/>) and a <see cref="TimeProvider"/>, which
    /// <c>AddWaslaInfrastructure</c> registers. The host does not start without <c>Wasla:Contact:RecipientEmail</c>.
    /// </summary>
    public static IServiceCollection AddWaslaContactMessages(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ContactOptions>()
            .Bind(configuration.GetSection(ContactOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ContactOptions>, ContactOptionsValidator>());

        services.TryAddSingleton<ContactMessageEmailFactory>();
        services.TryAddScoped<IContactMessageService, ContactMessageService>();
        services.TryAddScoped<ICentralAdminContactMessageService, CentralAdminContactMessageService>();
        return services;
    }
}
