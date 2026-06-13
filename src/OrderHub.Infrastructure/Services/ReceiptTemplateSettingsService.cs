using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Printing;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class ReceiptTemplateSettingsService : IReceiptTemplateSettingsService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly IValidator<UpdateReceiptTemplateSettingsCommand> _validator;
    private readonly ILogger<ReceiptTemplateSettingsService> _logger;

    public ReceiptTemplateSettingsService(
        ICustomerDbContextFactory dbFactory,
        IValidator<UpdateReceiptTemplateSettingsCommand> validator,
        ILogger<ReceiptTemplateSettingsService> logger)
    {
        _dbFactory = dbFactory;
        _validator = validator;
        _logger = logger;
    }

    public async Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.CustomerOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        return Deserialize(row?.ReceiptTemplateSettingsJson, customerDisplayName);
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

        var row = await db.CustomerOperationalSettings
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new CustomerOperationalSettings
            {
                Id = SingletonId,
                OrderSyncEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ReceiptTemplateSettingsJson = Serialize(settings)
            };
            db.CustomerOperationalSettings.Add(row);
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

    private static ReceiptTemplateSettings Deserialize(string? json, string? customerDisplayName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ReceiptTemplateSettings.CreateDefaults(customerDisplayName);

        try
        {
            var settings = JsonSerializer.Deserialize<ReceiptTemplateSettings>(json, JsonOptions);
            return settings ?? ReceiptTemplateSettings.CreateDefaults(customerDisplayName);
        }
        catch (JsonException)
        {
            return ReceiptTemplateSettings.CreateDefaults(customerDisplayName);
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
            ReceiptHeaderText = ReceiptTemplateSettings.NormalizeSingleLine(command.ReceiptHeaderText, ReceiptTemplateLimits.HeaderMaxLength),
            ReceiptFooterText = ReceiptTemplateSettings.NormalizeFooter(command.ReceiptFooterText)
        };
}
