using System.Globalization;
using System.Text.Json;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Tests.WebShell;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-44: the printed receipt shows the server's items in the server's order and every amount in one format, whatever
/// the culture of the Windows PC that prints it. Amounts are plain numbers with two decimals and a point (as the
/// receipt preview in Web shows them); the receipt names no currency, so it never shows one taken from the PC's culture.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ReceiptAmountFormattingTests : IDisposable
{
    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    [Theory]
    [InlineData(SupportedCultures.Turkish)]
    [InlineData(SupportedCultures.English)]
    [InlineData(SupportedCultures.Arabic)]
    [InlineData(SupportedCultures.Russian)]
    public void Receipt_UsesOneAmountFormat_AndTheServerOrder_OnAnyPcCulture(string pcCulture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(pcCulture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(pcCulture);

        var receipt = new ReceiptFormatter().Format(Payload(), normalizeTurkishChars: false);

        foreach (var amount in new[] { "25.00", "12.50", "3.75", "40.00", "75.00", "15.00", "92.25" })
            Assert.Contains(amount, receipt, StringComparison.Ordinal);
        foreach (var culturePrinted in new[] { "12,50", "3,75", "25,00", "92,25", "٫" })
            Assert.DoesNotContain(culturePrinted, receipt, StringComparison.Ordinal);
        foreach (var currency in new[] { "₺", "TL", "TRY", "$", "₽", "ر.س", "SAR", "RUB", "USD" })
            Assert.DoesNotContain(currency, receipt, StringComparison.Ordinal);

        AssertInOrder(receipt, "1x Mercimek Çorbası", "2x Adana Kebap", "1x Ayran");
        AssertInOrder(receipt, "Ekstra lavaş (12.50)", "Acılı", "Yoğurtlu (3.75)");
    }

    private static void AssertInOrder(string receipt, params string[] parts)
    {
        var previous = -1;
        foreach (var part in parts)
        {
            var at = receipt.IndexOf(part, StringComparison.Ordinal);
            Assert.True(at > previous, $"'{part}' is missing or out of order in:{Environment.NewLine}{receipt}");
            previous = at;
        }
    }

    private static string Payload() =>
        JsonSerializer.Serialize(new
        {
            ExternalOrderCode = "TY-1001",
            Subtotal = 92.25m,
            DeliveryFee = 15m,
            TotalAmount = 107.25m,
            Template = new { Language = "tr", ShowProductOptions = true, ShowSubtotal = true, ShowDeliveryFee = true },
            Items = new object[]
            {
                new
                {
                    ProductName = "Mercimek Çorbası",
                    Quantity = 1,
                    LineTotal = 25m,
                    Options = new object[]
                    {
                        new { Name = "Ekstra lavaş", Price = 12.5m },
                        new { Name = "Acılı", Price = 0m },
                        new { Name = "Yoğurtlu", Price = 3.75m }
                    }
                },
                new { ProductName = "Adana Kebap", Quantity = 2, LineTotal = 40m },
                new { ProductName = "Ayran", Quantity = 1, LineTotal = 75m }
            }
        });
}
