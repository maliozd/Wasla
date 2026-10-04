using System.ComponentModel.DataAnnotations;
using Wasla.Web.Validation;

namespace Wasla.UnitTests.Validation;

public sealed class OptionalEmailAddressAttributeTests
{
    [Fact]
    public void IsValid_AcceptsNullEmptyAndWhitespaceValues()
    {
        var attribute = new OptionalEmailAddressAttribute();

        Assert.True(attribute.IsValid(null));
        Assert.True(attribute.IsValid(string.Empty));
        Assert.True(attribute.IsValid("   "));
    }

    [Fact]
    public void GetValidationResult_AcceptsValidEmailAndRejectsInvalidNonEmptyEmail()
    {
        var attribute = new OptionalEmailAddressAttribute();
        var context = new ValidationContext(new object()) { DisplayName = "Business email" };

        Assert.Equal(ValidationResult.Success, attribute.GetValidationResult("owner@wasla.local", context));

        var invalidResult = attribute.GetValidationResult("not an email", context);

        Assert.NotNull(invalidResult);
        Assert.Equal("Validation.EmailInvalid", invalidResult.ErrorMessage);
    }
}
