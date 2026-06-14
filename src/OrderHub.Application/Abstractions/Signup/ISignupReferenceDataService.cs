namespace OrderHub.Application.Abstractions.Signup;

public interface ISignupReferenceDataService
{
    Task<IReadOnlyList<SignupBusinessTypeOption>> GetActiveBusinessTypesAsync(CancellationToken ct);

    Task<IReadOnlyList<SignupCityOption>> GetActiveCitiesAsync(string countryCode, CancellationToken ct);

    Task<IReadOnlyList<SignupDistrictOption>> GetDistrictsByCityIdAsync(int cityId, CancellationToken ct);

    Task<SignupCityOption?> GetCityByIdAsync(int cityId, CancellationToken ct);

    Task<IReadOnlyList<SignupBusinessTypeOption>> ResolveBusinessTypesByCodesAsync(
        IReadOnlyList<string> codes,
        CancellationToken ct);

    Task<SignupCityDistrictNames?> ResolveCityDistrictAsync(
        int cityId,
        int districtId,
        string countryCode,
        CancellationToken ct);
}
