namespace OrderHub.Application.Abstractions.Signup;

public sealed record SignupBusinessTypeOption(string Code, string DisplayName);

public sealed record SignupCityOption(int Id, string Name, string? PhoneAreaCode);

public sealed record SignupDistrictOption(int Id, string Name);

public sealed record SignupCityDistrictNames(int CityId, string CityName, int DistrictId, string DistrictName);
