using System.Globalization;
using Wasla.Application.Abstractions.Printing;

namespace Wasla.Application.Printing;

/// <summary>
/// Receipt field labels for printed receipts and preview. Independent from web UI culture.
/// </summary>
public static class ReceiptLabelLocalizer
{
    public const string Order = "order";
    public const string Platform = "platform";
    public const string Received = "received";
    public const string Customer = "customer";
    public const string Phone = "phone";
    public const string Address = "address";
    public const string Line = "line";
    public const string Note = "note";
    public const string Subtotal = "subtotal";
    public const string Delivery = "delivery";
    public const string Discount = "discount";
    public const string Total = "total";
    public const string Payment = "payment";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [ReceiptLanguageCodes.Turkish] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Order] = "Sipariş",
                [Platform] = "Platform",
                [Received] = "Alınma",
                [Customer] = "Müşteri",
                [Phone] = "Telefon",
                [Address] = "Adres",
                [Line] = "Satır",
                [Note] = "Not",
                [Subtotal] = "Ara Toplam",
                [Delivery] = "Teslimat",
                [Discount] = "İndirim",
                [Total] = "TOPLAM",
                [Payment] = "Ödeme"
            },
            [ReceiptLanguageCodes.English] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Order] = "Order",
                [Platform] = "Platform",
                [Received] = "Received",
                [Customer] = "Customer",
                [Phone] = "Phone",
                [Address] = "Address",
                [Line] = "Line",
                [Note] = "Note",
                [Subtotal] = "Subtotal",
                [Delivery] = "Delivery",
                [Discount] = "Discount",
                [Total] = "TOTAL",
                [Payment] = "Payment"
            },
            [ReceiptLanguageCodes.Arabic] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Order] = "الطلب",
                [Platform] = "المنصة",
                [Received] = "وقت الاستلام",
                [Customer] = "العميل",
                [Phone] = "الهاتف",
                [Address] = "العنوان",
                [Line] = "السطر",
                [Note] = "ملاحظة",
                [Subtotal] = "المجموع الفرعي",
                [Delivery] = "رسوم التوصيل",
                [Discount] = "الخصم",
                [Total] = "الإجمالي",
                [Payment] = "الدفع"
            },
            [ReceiptLanguageCodes.Russian] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Order] = "Заказ",
                [Platform] = "Платформа",
                [Received] = "Получен",
                [Customer] = "Клиент",
                [Phone] = "Телефон",
                [Address] = "Адрес",
                [Line] = "Строка",
                [Note] = "Примечание",
                [Subtotal] = "Промежуточный итог",
                [Delivery] = "Доставка",
                [Discount] = "Скидка",
                [Total] = "ИТОГО",
                [Payment] = "Оплата"
            }
        };

    private static readonly IReadOnlyDictionary<string, string> DefaultFooters =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReceiptLanguageCodes.Turkish] = "Bizi tercih ettiğiniz için teşekkür ederiz.",
            [ReceiptLanguageCodes.English] = "Thank you for your order.",
            [ReceiptLanguageCodes.Arabic] = "شكراً لطلبكم.",
            [ReceiptLanguageCodes.Russian] = "Спасибо за ваш заказ."
        };

    public static string GetLabel(string language, string key)
    {
        var lang = ReceiptLanguageCodes.Normalize(language);
        if (Labels.TryGetValue(lang, out var map) && map.TryGetValue(key, out var value))
            return value;
        return Labels[ReceiptLanguageCodes.Turkish][key];
    }

    public static string GetDefaultFooter(string? language) =>
        DefaultFooters.TryGetValue(ReceiptLanguageCodes.Normalize(language), out var footer)
            ? footer
            : DefaultFooters[ReceiptLanguageCodes.Turkish];

    public static bool IsKnownDefaultFooter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var normalized = text.Trim();
        return DefaultFooters.Values.Any(v => string.Equals(v, normalized, StringComparison.Ordinal));
    }

    /// <summary>Receipt culture with the Gregorian calendar, so printed order times match provider records.</summary>
    public static CultureInfo GetCulture(string? language) =>
        GregorianCulture.For(ReceiptLanguageCodes.Normalize(language) switch
        {
            ReceiptLanguageCodes.English => CultureInfo.GetCultureInfo("en-US"),
            ReceiptLanguageCodes.Arabic => CultureInfo.GetCultureInfo("ar-SA"),
            ReceiptLanguageCodes.Russian => CultureInfo.GetCultureInfo("ru-RU"),
            _ => CultureInfo.GetCultureInfo("tr-TR")
        });

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetAllLabels() => Labels;

    public static IReadOnlyDictionary<string, string> GetDefaultFooters() => DefaultFooters;
}
