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
