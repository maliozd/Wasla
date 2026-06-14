namespace Wasla.Application.Abstractions.Signup;

public interface ISignupReferenceDataService
{
    Task<IReadOnlyList<SignupBusinessTypeOption>> GetActiveBusinessTypesAsync(CancellationToken ct);

    Task<IReadOnlyList<SignupCityOption>> GetActiveCitiesAsync(string countryCode, CancellationToken ct);

    Task<IReadOnlyList<SignupDistrictOption>> GetDistrictsByCityIdAsync(int cityId, CancellationToken ct);

    Task<IReadOnlyList<SignupNeighborhoodOption>> GetNeighborhoodsByDistrictIdAsync(int districtId, CancellationToken ct);

    Task<IReadOnlyList<SignupStreetOption>> GetStreetsByNeighborhoodIdAsync(int neighborhoodId, CancellationToken ct);

    Task<SignupCityOption?> GetCityByIdAsync(int cityId, CancellationToken ct);

    Task<IReadOnlyList<SignupBusinessTypeOption>> ResolveBusinessTypesByCodesAsync(
        IReadOnlyList<string> codes,
        CancellationToken ct);

    Task<SignupCityDistrictNames?> ResolveCityDistrictAsync(
        int cityId,
        int districtId,
        string countryCode,
        CancellationToken ct);

    Task<SignupNeighborhoodNames?> ResolveNeighborhoodAsync(
        int districtId,
        int neighborhoodId,
        CancellationToken ct);

    Task<SignupStreetNames?> ResolveStreetAsync(
        int neighborhoodId,
        int streetId,
        CancellationToken ct);
}
