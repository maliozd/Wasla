namespace Wasla.Domain.Entities.Central;

/// <summary>The stored lengths of a <see cref="ContactMessage"/>, shared by the form, the entity and the CentralDb columns.</summary>
public static class ContactMessageLimits
{
    public const int FullNameMaxLength = 120;
    public const int RestaurantNameMaxLength = 160;
    public const int EmailMaxLength = 254;

    /// <summary>A Turkish phone number without the country code: ten digits.</summary>
    public const int PhoneDigits = 10;

    public const int MessageMaxLength = 2000;
    public const int CultureMaxLength = 10;
}
