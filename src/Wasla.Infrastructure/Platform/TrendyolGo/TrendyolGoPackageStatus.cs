using System.Collections.Frozen;

namespace Wasla.Infrastructure.Platform.TrendyolGo;

/// <summary>
/// The Trendyol GO package statuses Wasla recognizes, and the one rule for comparing a raw <c>packageStatus</c> with
/// them. These are provider facts only: the Trendyol GO client uses them to warn about a status nobody recognizes, and
/// Orders decides which order status each recognized status means (<c>DefaultOrderStatusMapper</c>).
/// </summary>
/// <remarks>
/// A raw status is trimmed (<see cref="string.Trim()"/>) and then compared case-sensitively with the spellings below,
/// so another casing of a known status is not recognized. The one exception is <see cref="Returned"/>, which is
/// recognized in any casing. A missing or blank status is not a status: it is neither recognized nor unknown.
/// </remarks>
internal static class TrendyolGoPackageStatus
{
    // The packageStatuses values the official documentation lists, with their casing.
    public const string Created = "Created";
    public const string Picking = "Picking";
    public const string Invoiced = "Invoiced";
    public const string Cancelled = "Cancelled";
    public const string UnSupplied = "UnSupplied";
    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";

    /// <summary>Not a value of the fetch filter, but recognized in any casing.</summary>
    public const string Returned = "Returned";

    // Labels earlier mock data used: "New" and Turkish words, each recognized in the spellings listed in Spellings.
    public const string New = "New";
    public const string TurkishNew = "Yeni";
    public const string TurkishApproved = "Onaylandı";
    public const string TurkishPreparing = "Hazırlanıyor";
    public const string TurkishReady = "Hazır";
    public const string TurkishOnTheWay = "Yolda";
    public const string TurkishDelivery = "Teslim";
    public const string TurkishDelivered = "Teslim edildi";
    public const string TurkishCancel = "İptal";
    public const string TurkishCancelled = "İptal edildi";
    public const string TurkishFailed = "Başarısız";

    /// <summary>The documented statuses in the documentation's order: the client's <c>packageStatuses</c> filter.</summary>
    public static IReadOnlyList<string> Documented { get; } =
        [Created, Picking, Invoiced, Cancelled, UnSupplied, Shipped, Delivered];

    /// <summary>Each accepted spelling and the status it spells. Compared ordinally, so case-sensitively.</summary>
    private static readonly FrozenDictionary<string, string> Spellings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Created] = Created,
        [Picking] = Picking,
        [Invoiced] = Invoiced,
        [Cancelled] = Cancelled,
        [UnSupplied] = UnSupplied,
        [Shipped] = Shipped,
        [Delivered] = Delivered,

        [New] = New,
        ["new"] = New,
        [TurkishNew] = TurkishNew,
        ["yeni"] = TurkishNew,
        [TurkishApproved] = TurkishApproved,
        ["Onaylandi"] = TurkishApproved,
        ["onaylandı"] = TurkishApproved,
        ["onaylandi"] = TurkishApproved,
        [TurkishPreparing] = TurkishPreparing,
        ["Hazirlaniyor"] = TurkishPreparing,
        ["hazırlanıyor"] = TurkishPreparing,
        ["hazirlaniyor"] = TurkishPreparing,
        [TurkishReady] = TurkishReady,
        ["Hazir"] = TurkishReady,
        ["hazır"] = TurkishReady,
        ["hazir"] = TurkishReady,
        [TurkishOnTheWay] = TurkishOnTheWay,
        ["yolda"] = TurkishOnTheWay,
        [TurkishDelivery] = TurkishDelivery,
        ["teslim"] = TurkishDelivery,
        [TurkishDelivered] = TurkishDelivered,
        ["teslim edildi"] = TurkishDelivered,
        [TurkishCancel] = TurkishCancel,
        ["Iptal"] = TurkishCancel,
        ["iptal"] = TurkishCancel,
        [TurkishCancelled] = TurkishCancelled,
        ["Iptal edildi"] = TurkishCancelled,
        ["iptal edildi"] = TurkishCancelled,
        [TurkishFailed] = TurkishFailed,
        ["Basarisiz"] = TurkishFailed,
        ["başarısız"] = TurkishFailed,
        ["basarisiz"] = TurkishFailed,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every status <see cref="Recognize"/> can return, each once.</summary>
    public static IReadOnlyCollection<string> All { get; } = Spellings.Values.Append(Returned).Distinct().ToArray();

    /// <summary>A raw status as it is compared and logged: trimmed, and empty when missing.</summary>
    public static string Normalize(string? rawStatus) => (rawStatus ?? string.Empty).Trim();

    /// <summary>
    /// The recognized status a raw value spells (one of <see cref="All"/>), or <see langword="null"/> when the value is
    /// blank or not recognized.
    /// </summary>
    public static string? Recognize(string? rawStatus)
    {
        var status = Normalize(rawStatus);
        if (status.Length == 0)
            return null;
        if (status.Equals(Returned, StringComparison.OrdinalIgnoreCase))
            return Returned;
        return Spellings.GetValueOrDefault(status);
    }
}
