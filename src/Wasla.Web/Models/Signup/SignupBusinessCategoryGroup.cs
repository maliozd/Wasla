namespace Wasla.Web.Models.Signup;

public sealed record SignupBusinessSubtypeOption(string Code, string Label, bool Selected);

public sealed record SignupBusinessCategoryGroup(
    string CategoryCode,
    string Label,
    IReadOnlyList<SignupBusinessSubtypeOption> Subtypes);
