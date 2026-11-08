using System.Globalization;
using System.Net;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// A browser that goes through the registration draft API of a <see cref="SignupWebHost"/> as the signup page's script
/// does (WAS-192): JSON writes with the antiforgery header, the draft cookie in its jar, and verification codes read from
/// the host's fake SMS outbox. All data is fake; every client uses its own mobile number, so the per-number send limits
/// of one client never affect another.
/// </summary>
internal sealed class SignupDraftClient : IDisposable
{
    public const string Password = "Fake-pass-192";

    private readonly SignupWebHost _host;
    private string? _antiforgeryToken;

    public SignupDraftClient(SignupWebHost host, string hostName = SignupWebHost.CentralHost)
    {
        _host = host;
        HostName = hostName;
        Browser = host.NewBrowser();
        MobileE164 = NewMobileE164();
    }

    public TestBrowser Browser { get; }

    public string HostName { get; }

    /// <summary>The owner mobile number this client saves by default, in E.164.</summary>
    public string MobileE164 { get; set; }

    /// <summary>The same number as typed by a Turkish user: <c>0532 123 45 67</c>.</summary>
    public string MobileTyped => "0" + MobileE164[3..6] + " " + MobileE164[6..9] + " " + MobileE164[9..11] + " " + MobileE164[11..];

    public string AntiforgeryToken => _antiforgeryToken ??= _host.IssueAntiforgeryToken(Browser, HostName);

    public static string NewMobileE164() =>
        "+905" + Random.Shared.Next(0, 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);

    public static string NewSlug() => $"s{SentinelApplicant.NewToken()}".ToLowerInvariant();

    public Task<TestResponse> GetDraftAsync() => Browser.GetAsync("/signup/draft", HostName);

    public Task<TestResponse> GetPhoneAsync() => Browser.GetAsync("/signup/draft/phone", HostName);

    public Task<TestResponse> PutAsync(string path, object? body, bool antiforgery = true) =>
        Browser.SendJsonAsync(HttpMethod.Put, path, body, antiforgery ? AntiforgeryToken : null, HostName);

    public Task<TestResponse> PostAsync(string path, object? body = null, bool antiforgery = true) =>
        Browser.SendJsonAsync(HttpMethod.Post, path, body, antiforgery ? AntiforgeryToken : null, HostName);

    public Task<TestResponse> SavePlanAsync(string planCode = "Starter", string billingPeriod = "Monthly") =>
        PutAsync("/signup/draft/plan", new Dictionary<string, object?> { ["planCode"] = planCode, ["billingPeriod"] = billingPeriod });

    public static Dictionary<string, object?> Restaurant(string? slug = null) => new()
    {
        ["businessName"] = $"Bizname{SentinelApplicant.NewToken()}",
        ["businessTypeCodes"] = new[] { "burger" },
        ["businessPhoneType"] = "Landline",
        ["businessPhone"] = "212 555 12 34",
        ["slug"] = slug ?? NewSlug(),
        ["country"] = "Germany",
        ["city"] = "Berlin",
        ["district"] = "Mitte",
        ["streetAddress"] = $"Streetq{SentinelApplicant.NewToken()}"
    };

    public Task<TestResponse> SaveRestaurantAsync(Dictionary<string, object?>? body = null) =>
        PutAsync("/signup/draft/restaurant", body ?? Restaurant());

    public Dictionary<string, object?> Owner(string? email = null, string? mobile = null, string? password = Password) => new()
    {
        ["fullName"] = $"Ownerq{SentinelApplicant.NewToken()}",
        ["email"] = email ?? $"owner.{SentinelApplicant.NewToken()}@sentinel.test",
        ["mobilePhone"] = mobile ?? MobileTyped,
        ["password"] = password,
        ["confirmPassword"] = password
    };

    public Task<TestResponse> SaveOwnerAsync(Dictionary<string, object?>? body = null) =>
        PutAsync("/signup/draft/owner", body ?? Owner());

    public Task<TestResponse> SendCodeAsync() => PostAsync("/signup/draft/phone/send");

    public Task<TestResponse> VerifyAsync(string? code) => PostAsync("/signup/draft/phone/verify", new Dictionary<string, object?> { ["code"] = code });

    public Task<TestResponse> ChangePhoneAsync(string mobilePhone) =>
        PutAsync("/signup/draft/phone", new Dictionary<string, object?> { ["mobilePhone"] = mobilePhone });

    public Task<TestResponse> SubmitAsync(object? body = null) => PostAsync("/signup/draft/submit", body);

    /// <summary>The newest fake code sent to <paramref name="e164"/> (default: this client's number).</summary>
    public string LatestCode(string? e164 = null) =>
        _host.FakeSms!.LatestCodeTo(e164 ?? MobileE164) ?? throw new InvalidOperationException("No code was sent to the number.");

    /// <summary>Plan, restaurant and owner saved; each answer must be 200.</summary>
    public async Task SaveSectionsAsync(string? slug = null, string? email = null)
    {
        await AssertOkAsync(SavePlanAsync());
        await AssertOkAsync(SaveRestaurantAsync(Restaurant(slug)));
        await AssertOkAsync(SaveOwnerAsync(Owner(email)));
    }

    /// <summary>Saves every section, verifies the owner's number with the fake code and returns the submit answer.</summary>
    public async Task<TestResponse> CompleteAsync(string? slug = null, string? email = null)
    {
        await SaveSectionsAsync(slug, email);
        await AssertOkAsync(SendCodeAsync());
        await AssertOkAsync(VerifyAsync(LatestCode()));
        return await SubmitAsync();
    }

    /// <summary>As <see cref="CompleteAsync"/>, and the submit must succeed; returns the registration id.</summary>
    public async Task<Guid> RegisterAsync(string? slug = null, string? email = null)
    {
        var submitted = await CompleteAsync(slug, email);
        Assert.True(submitted.Status == HttpStatusCode.OK, $"Submit answered {(int)submitted.Status}: {CodeOf(submitted)}");
        return submitted.Json.GetProperty("registrationId").GetGuid();
    }

    public static async Task<TestResponse> AssertOkAsync(Task<TestResponse> request)
    {
        var response = await request;
        Assert.True(response.Status == HttpStatusCode.OK, $"Expected 200, got {(int)response.Status}: {CodeOf(response)}");
        return response;
    }

    /// <summary>The error code of a JSON error answer, or the status for any other answer. Never a message.</summary>
    public static string CodeOf(TestResponse response) =>
        response.ContentType == "application/json" && response.Json.TryGetProperty("code", out var code)
            ? code.GetString() ?? string.Empty
            : $"(no JSON code, status {(int)response.Status})";

    public void Dispose() => Browser.Dispose();
}
