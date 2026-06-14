namespace Wasla.Application.Abstractions.Signup;

public sealed record SignupBusinessTypeOption(string Code, string DisplayName);

public sealed record SignupCityOption(int Id, string Name, string? PhoneAreaCode);

public sealed record SignupDistrictOption(int Id, string Name);

public sealed record SignupNeighborhoodOption(int Id, string Name);

public sealed record SignupStreetOption(int Id, string Name, string? StreetType);

public sealed record SignupCityDistrictNames(int CityId, string CityName, int DistrictId, string DistrictName);

public sealed record SignupNeighborhoodNames(int NeighborhoodId, string NeighborhoodName);

public sealed record SignupStreetNames(int StreetId, string StreetName, string? StreetType);
