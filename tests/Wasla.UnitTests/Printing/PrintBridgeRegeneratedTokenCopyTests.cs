using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wasla.Domain.Entities.Central;
using Wasla.Web.Models.PrintBridge;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// The device details note about token visibility (<c>PrintBridge.DeviceTokenSafeNote</c>). It used to say the raw token
/// is never shown on the page, which stopped being true once "Regenerate token" showed the new token once, masked.
/// These tests keep the note truthful in every culture and tied to what the page and the stored data actually do.
/// </summary>
public sealed partial class PrintBridgeRegeneratedTokenCopyTests
{
    private const string NoteKey = "PrintBridge.DeviceTokenSafeNote";

    // Neutral resolves through the Turkish UI labels; every other culture uses its own.
    public static TheoryData<string, string> Cultures => new()
    {
        { "", ".tr-TR" }, { ".tr-TR", ".tr-TR" }, { ".en-US", ".en-US" }, { ".ar-SA", ".ar-SA" }, { ".ru-RU", ".ru-RU" }
    };

    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void Note_NoLongerClaimsTheTokenIsNeverShownOnThePage(string culture)
    {
        var note = Load(culture)[NoteKey];

        foreach (var contradiction in new[]
                 {
                     "not shown on this page", "token hash", "bu sayfada gösterilmez", "token hash'i",
                     "لا يتم عرض رمز الجهاز الخام", "تجزئة الرمز", "на этой странице не отображаются", "хэш токена"
                 })
            Assert.DoesNotContain(contradiction, note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Note_NamesTheRealControls_ItDescribes(string culture, string uiCulture)
    {
        var note = Load(culture)[NoteKey];
        var ui = Load(uiCulture);

        // Regenerate, the Show/Hide toggle and Copy, exactly as the device details page labels them.
        Assert.Contains(ui["PrintBridge.RegenerateToken"], note, StringComparison.Ordinal);
        Assert.Contains(ui["PrintBridge.Manual.ShowToken"], note, StringComparison.Ordinal);
        Assert.Contains(ui["PrintBridge.Manual.HideToken"], note, StringComparison.Ordinal);
        Assert.Contains(ui["PrintBridge.CopyToken"], note, StringComparison.Ordinal);
    }

    [Theory]
    // Each row: the existing token is never shown again; regenerating revokes the old one at once; the new one is shown
    // only once; it starts masked; Show/Hide only changes how it looks; Copy works while masked; store it securely; and
    // after closing, leaving or reloading it cannot be shown again.
    [InlineData(".en-US", new[]
    {
        "current device token can never be shown again", "old one stops working immediately", "shown only once", "starts masked",
        "only changes how it looks", "works while it stays masked", "Store it securely", "close the box, leave or reload this page, it cannot be shown again"
    })]
    [InlineData(".tr-TR", new[]
    {
        "Mevcut cihaz tokenı bir daha gösterilemez", "eski token hemen geçersiz olur", "yalnızca bir kez gösterilir", "maskeli başlar",
        "yalnızca görünümünü değiştirir", "token maskeliyken de çalışır", "güvenli bir yerde saklayın", "kutuyu kapattıktan, sayfadan ayrıldıktan veya sayfayı yeniledikten sonra bir daha gösterilemez"
    })]
    [InlineData("", new[]
    {
        "Mevcut cihaz tokenı bir daha gösterilemez", "eski token hemen geçersiz olur", "yalnızca bir kez gösterilir", "maskeli başlar",
        "yalnızca görünümünü değiştirir", "token maskeliyken de çalışır", "güvenli bir yerde saklayın", "kutuyu kapattıktan, sayfadan ayrıldıktan veya sayfayı yeniledikten sonra bir daha gösterilemez"
    })]
    [InlineData(".ar-SA", new[]
    {
        "لا يمكن عرض رمز الجهاز الحالي مرة أخرى", "يتوقف الرمز القديم عن العمل فورًا", "مرة واحدة فقط", "يبدأ مخفيًا",
        "يغيّر طريقة عرضه فقط", "وهو مخفي", "احفظه في مكان آمن", "بعد إغلاق المربع أو مغادرة هذه الصفحة أو إعادة تحميلها لا يمكن عرضه مرة أخرى"
    })]
    [InlineData(".ru-RU", new[]
    {
        "Текущий токен устройства больше нельзя показать", "старый сразу перестаёт работать", "только один раз", "сначала скрыт",
        "меняет только его отображение", "работает, пока токен скрыт", "Сохраните его в надёжном месте", "после закрытия окна, ухода со страницы или её перезагрузки его нельзя будет показать снова"
    })]
    public void Note_StatesTheSameEightSecurityPoints_InEveryCulture(string culture, string[] points)
    {
        var note = Load(culture)[NoteKey];

        Assert.Equal(8, points.Length);
        foreach (var point in points)
            Assert.Contains(point, note, StringComparison.Ordinal);
    }

    [Fact]
    public void Note_IsOneUniqueConciseKey_WithMatchingPlaceholders()
    {
        var english = Load(".en-US")[NoteKey];
        foreach (var culture in new[] { "", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU" })
        {
            var raw = File.ReadAllText(ResourcePath(culture));
            Assert.Single(Regex.Matches(raw, $"<data name=\"{Regex.Escape(NoteKey)}\""));
            var note = Load(culture)[NoteKey];
            Assert.Equal(Placeholders(english), Placeholders(note));
            // Short enough for the page: four sentences, no security essay.
            Assert.InRange(note.Length, 200, 450);
            Assert.Equal(4, Regex.Matches(note, @"[.。](\s|$)").Count);
        }
    }

    [Fact]
    public void Note_AgreesWithTheDetailsPage_OneTimeMaskedTokenThatIsClearedOnDismiss()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "DeviceDetails.cshtml"));

        Assert.Contains($"@L[\"{NoteKey}\"]", view, StringComparison.Ordinal);
        // The only place the new token appears: a read-only field that starts masked, with the Show/Hide toggle and Copy.
        Assert.Matches(new Regex(@"<input type=""password""\s+class=""[^""]*""\s+id=""printBridgeDetailTokenValue""[^>]*readonly />"), view);
        Assert.Contains("id=\"printBridgeDetailTokenToggleBtn\"", view, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"printBridgeDetailTokenValue\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"printBridgeDetailCopyTokenBtn\"", view, StringComparison.Ordinal);
        // The value only arrives from the regenerate response and is cleared again on dismiss (behaviour is covered by
        // print-bridge-device-details-token.test.js); the server never renders a token into the page.
        Assert.Matches(new Regex(@"setTokenMasked\(true\);\s+value\.value = currentToken;"), view);
        Assert.Matches(new Regex(@"if \(value\) value\.value = """";"), view);
        Assert.DoesNotContain("Model.Token", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTokens_CannotBeRetrieved_TheDetailsModelAndTheStoredDeviceHoldNoRawToken()
    {
        // The details page model only knows whether a token exists.
        var modelTokenProperties = typeof(PrintBridgeDeviceDetailsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var hasToken = Assert.Single(modelTokenProperties);
        Assert.Equal(nameof(PrintBridgeDeviceDetailsViewModel.HasToken), hasToken.Name);
        Assert.Equal(typeof(bool), hasToken.PropertyType);

        // The stored device keeps only a hash, so an existing token can never be shown again.
        var entityTokenProperties = typeof(PrintBridgeDevice)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToArray();
        Assert.Equal(new[] { nameof(PrintBridgeDevice.TokenHash) }, entityTokenProperties);
    }

    private static IEnumerable<string> Placeholders(string value) => PlaceholderPattern().Matches(value).Select(m => m.Value);

    private static string ResourcePath(string culture) =>
        Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx");

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(ResourcePath(culture))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value.Trim() ?? string.Empty, StringComparer.Ordinal);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
