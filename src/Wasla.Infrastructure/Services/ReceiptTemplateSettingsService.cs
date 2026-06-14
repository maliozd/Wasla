using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class ReceiptTemplateSettingsService : IReceiptTemplateSettingsService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IValidator<UpdateReceiptTemplateSettingsCommand> _validator;
    private readonly ILogger<ReceiptTemplateSettingsService> _logger;

    public ReceiptTemplateSettingsService(
        ITenantDbContextFactory dbFactory,
        IValidator<UpdateReceiptTemplateSettingsCommand> validator,
        ILogger<ReceiptTemplateSettingsService> logger)
    {
        _dbFactory = dbFactory;
        _validator = validator;
        _logger = logger;
    }

    public async Task<ReceiptTemplateSettings> GetAsync(
        Guid customerId,
        string? customerDisplayName,
        string? defaultReceiptLanguage,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.TenantOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        return Deserialize(row?.ReceiptTemplateSettingsJson, customerDisplayName, defaultReceiptLanguage);
    }

    public async Task<ReceiptTemplateSettings> UpdateAsync(
        Guid customerId,
        string? customerDisplayName,
        UpdateReceiptTemplateSettingsCommand command,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(command, ct).ConfigureAwait(false);

        var settings = MapCommand(command);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.TenantOperationalSettings
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new TenantOperationalSettings
            {
                Id = SingletonId,
                OrderSyncEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ReceiptTemplateSettingsJson = Serialize(settings)
            };
            db.TenantOperationalSettings.Add(row);
        }
        else
        {
            row.ReceiptTemplateSettingsJson = Serialize(settings);
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Receipt template settings updated. CustomerId={CustomerId}", customerId);

        return settings;
    }

    private static ReceiptTemplateSettings Deserialize(string? json, string? customerDisplayName, string? defaultReceiptLanguage)
    {
        var fallbackLanguage = ReceiptLanguageCodes.Normalize(defaultReceiptLanguage);

        if (string.IsNullOrWhiteSpace(json))
            return ReceiptTemplateSettings.CreateDefaults(customerDisplayName, fallbackLanguage);

        try
        {
            var settings = JsonSerializer.Deserialize<ReceiptTemplateSettings>(json, JsonOptions);
            if (settings is null)
                return ReceiptTemplateSettings.CreateDefaults(customerDisplayName, fallbackLanguage);

            NormalizeLegacyHeaderText(settings, customerDisplayName);
            settings.ReceiptLanguage = ReceiptLanguageCodes.Normalize(settings.ReceiptLanguage, fallbackLanguage);
            return settings;
        }
        catch (JsonException)
        {
            return ReceiptTemplateSettings.CreateDefaults(customerDisplayName, fallbackLanguage);
        }
    }

    private static void NormalizeLegacyHeaderText(ReceiptTemplateSettings settings, string? customerDisplayName)
    {
        // Older defaults stored tenant display name in ReceiptHeaderText, which bypassed ShowRestaurantName.
        if (string.IsNullOrWhiteSpace(customerDisplayName) || string.IsNullOrWhiteSpace(settings.ReceiptHeaderText))
            return;

        if (string.Equals(
                settings.ReceiptHeaderText.Trim(),
                customerDisplayName.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            settings.ReceiptHeaderText = null;
        }
    }

    private static string Serialize(ReceiptTemplateSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    private static ReceiptTemplateSettings MapCommand(UpdateReceiptTemplateSettingsCommand command) =>
        new()
        {
            ShowRestaurantName = command.ShowRestaurantName,
            ShowPlatformName = command.ShowPlatformName,
            ShowReceivedTime = command.ShowReceivedTime,
            ShowCustomerName = command.ShowCustomerName,
            ShowCustomerPhone = command.ShowCustomerPhone,
            ShowDeliveryAddress = command.ShowDeliveryAddress,
            ShowProductNotes = command.ShowProductNotes,
            ShowProductOptions = command.ShowProductOptions,
            ShowSubtotal = command.ShowSubtotal,
            ShowDiscount = false,
            ShowDeliveryFee = command.ShowDeliveryFee,
            ShowPaymentMethod = command.ShowPaymentMethod,
            ShowFooterMessage = command.ShowFooterMessage,
            ReceiptLanguage = ReceiptLanguageCodes.Normalize(command.ReceiptLanguage),
            ReceiptHeaderText = ReceiptTemplateSettings.NormalizeSingleLine(command.ReceiptHeaderText, ReceiptTemplateLimits.HeaderMaxLength),
            ReceiptFooterText = ReceiptTemplateSettings.NormalizeFooter(command.ReceiptFooterText)
        };
}
