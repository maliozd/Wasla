using Wasla.Domain.Enums;
using Wasla.Integrations.Platform;

namespace Wasla.Infrastructure.Platform;

/// <summary>
/// Orders' boundary with the provider library's enums (WAS-233). <c>Wasla.Integrations</c> owns
/// <see cref="ProviderPlatform"/>, <see cref="ProviderPaymentMethod"/> and <see cref="ProviderPaymentStatus"/>; they mirror
/// <see cref="FoodPlatform"/>, <see cref="PaymentMethod"/> and <see cref="PaymentStatus"/> member for member, with the same
/// names and numeric values (<c>ProviderEnumParityTests</c>). A conversion keeps the numeric value, so the value Orders
/// stores and the name it shows or writes to JSON mean the same on both sides of the boundary.
/// </summary>
internal static class ProviderEnumConversions
{
    public static FoodPlatform ToFoodPlatform(this ProviderPlatform platform) => (FoodPlatform)(int)platform;

    public static ProviderPlatform ToProviderPlatform(this FoodPlatform platform) => (ProviderPlatform)(int)platform;

    public static PaymentMethod ToPaymentMethod(this ProviderPaymentMethod method) => (PaymentMethod)(int)method;

    public static ProviderPaymentMethod ToProviderPaymentMethod(this PaymentMethod method) => (ProviderPaymentMethod)(int)method;

    public static PaymentStatus ToPaymentStatus(this ProviderPaymentStatus status) => (PaymentStatus)(int)status;

    public static ProviderPaymentStatus ToProviderPaymentStatus(this PaymentStatus status) => (ProviderPaymentStatus)(int)status;
}
