namespace Wasla.Application.Abstractions.ReferenceData;

/// <summary>A Turkish province of the CentralDb city catalog: its two-digit plate code and its Turkish name.</summary>
public sealed record TurkishProvince(string PlateCode, string Name);

/// <summary>
/// The active Turkish provinces of the CentralDb city catalog (the same <c>Cities</c> rows signup uses, seeded by the
/// reference-data import), keyed by plate code. The pilot application form lists them and stores the plate code.
/// </summary>
public interface ITurkishProvinceCatalog
{
    /// <summary>The active provinces with a plate code, ordered by their Turkish names.</summary>
    Task<IReadOnlyList<TurkishProvince>> GetActiveProvincesAsync(CancellationToken ct);
}

/// <summary>Whether the province catalog can serve the pilot form's city list.</summary>
public enum TurkishProvinceCatalogStatus
{
    /// <summary>All 81 plate codes, <c>01</c> to <c>81</c>, each on exactly one active province.</summary>
    Complete,

    /// <summary>No active province with a plate code: the reference data is not imported.</summary>
    Empty,

    /// <summary>Some active provinces, but not each of the 81 plate codes exactly once.</summary>
    Incomplete
}

public static class TurkishProvinces
{
    /// <summary>The number of Turkish provinces, plate codes <c>01</c> to <c>81</c>.</summary>
    public const int Count = 81;

    /// <summary>Whether <paramref name="provinces"/> is the complete list of Turkish provinces.</summary>
    public static TurkishProvinceCatalogStatus Check(IReadOnlyList<TurkishProvince> provinces)
    {
        ArgumentNullException.ThrowIfNull(provinces);
        if (provinces.Count == 0)
            return TurkishProvinceCatalogStatus.Empty;

        var expected = Enumerable.Range(1, Count).Select(n => n.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        return provinces.Count == Count && provinces.Select(p => p.PlateCode).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal)
            ? TurkishProvinceCatalogStatus.Complete
            : TurkishProvinceCatalogStatus.Incomplete;
    }
}
