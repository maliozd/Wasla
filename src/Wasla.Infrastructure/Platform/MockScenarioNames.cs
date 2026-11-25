using Wasla.Application.Signup;

namespace Wasla.Infrastructure.Platform;

/// <summary>
/// The mock scenario families that match a tenant's business subtypes (WAS-233). The mock clients in
/// <c>Wasla.Integrations</c> name their families after the restaurant types of their scenario files and know nothing of
/// Orders' signup catalog, so Orders hands them names: each subtype code the catalog knows becomes its subtype's name,
/// which is its family's name, in the given order. A code the catalog does not know is left out; the mocks use their
/// generic family when no name has a family.
/// </summary>
internal static class MockScenarioNames
{
    public static IReadOnlyList<string> ForSubtypeCodes(IEnumerable<string> subtypeCodes) =>
        subtypeCodes
            .Select(code => BusinessSubtypeCatalog.TryGet(code, out var definition) ? definition.Subtype.ToString() : null)
            .OfType<string>()
            .ToArray();
}
