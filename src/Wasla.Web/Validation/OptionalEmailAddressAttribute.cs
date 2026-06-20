using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Wasla.Web.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class OptionalEmailAddressAttribute : ValidationAttribute, IClientModelValidator
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    public OptionalEmailAddressAttribute()
    {
        ErrorMessage = "Validation.EmailInvalid";
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
            return ValidationResult.Success;

        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
            return ValidationResult.Success;

        return EmailValidator.IsValid(text.Trim())
            ? ValidationResult.Success
            : new ValidationResult(FormatErrorMessage(validationContext.DisplayName));
    }

    public void AddValidation(ClientModelValidationContext context)
    {
        MergeAttribute(context.Attributes, "data-val", "true");
        MergeAttribute(context.Attributes, "data-val-optionalemail", ErrorMessage ?? "Validation.EmailInvalid");
    }

    private static bool MergeAttribute(IDictionary<string, string> attributes, string key, string value)
    {
        if (attributes.ContainsKey(key))
            return false;

        attributes.Add(key, value);
        return true;
    }
}
