using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Wasla.Application.Abstractions.Admin;
using Wasla.Web.Areas.Admin.Controllers;
using Wasla.Web.Areas.Admin.Models.TenantOperations;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Source and type contracts that complement the HTTP tests: the authorization attribute, the absence of new write
/// endpoints, credential-free response models, accessibility markup and complete localization.
/// </summary>
public sealed partial class AdminTenantOperationsContractTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];

    private static readonly string[] ForbiddenMemberWords =
        ["Password", "Token", "Secret", "ConnectionString", "Encrypted", "ApiKey", "ErrorMessage", "Executor", "Supplier", "IpAddress", "Installation", "Payload"];

    [Theory]
    [InlineData(typeof(DashboardController))]
    [InlineData(typeof(CustomersController))]
    public void OperationsControllers_RequireTheCentralAdminScheme_WithNoAnonymousAction(Type controller)
    {
        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>().Single();
        Assert.Equal(AuthSchemes.CentralAdmin, authorize.AuthenticationSchemes);
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.All(
            controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    [Fact]
    public void OperationsCenter_AddsNoWriteEndpoint()
    {
        var writes = new[] { typeof(DashboardController), typeof(CustomersController) }
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any(a => !a.HttpMethods.SequenceEqual(["GET"])))
            .Select(method => method.Name)
            .Order()
            .ToList();

        // Only the activate/deactivate posts that existed before this page, both antiforgery-protected.
        Assert.Equal(["Activate", "Deactivate"], writes);
        Assert.All(writes, name => Assert.NotNull(typeof(CustomersController).GetMethod(name)!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }

    [Fact]
    public void ResponseModels_HaveNoCredentialShapedMembers()
    {
        var types = typeof(TenantOperationsOverview).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(TenantOperationsOverview).Namespace)
            .Where(t => t.Name.StartsWith("Tenant", StringComparison.Ordinal)
                || t.Name.StartsWith("Recent", StringComparison.Ordinal)
                || t.Name.StartsWith("Registration", StringComparison.Ordinal)
                || t.Name.StartsWith("PrintBridgeFleet", StringComparison.Ordinal))
            .Concat(typeof(AdminTenantDetailViewModel).Assembly.GetTypes()
                .Where(t => t.Namespace == typeof(AdminTenantDetailViewModel).Namespace))
            .ToList();

        Assert.Contains(typeof(TenantPlatformConnectionHealth), types);
        Assert.Contains(typeof(TenantOperationsDetail), types);
        foreach (var type in types)
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            Assert.DoesNotContain(ForbiddenMemberWords, word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdminOperationsViews_NeverReferenceSecretColumns()
    {
        var sources = AdminSources().ToList();
        Assert.True(sources.Count >= 8);

        foreach (var (path, text) in sources)
        foreach (var name in new[] { "EncryptedConnectionString", "TokenHash", "PasswordHash", "LastMigrationResult", "EncryptedApi", "ErrorMessage", "LastIpAddress", "SupplierId", "ExecutorEmail", "PayloadJson" })
            Assert.False(text.Contains(name, StringComparison.Ordinal), $"{path} references {name}");
    }

    [Fact]
    public void TenantList_MarkupIsAccessible()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Customers", "Index.cshtml");

        Assert.Contains("aria-sort=\"@ariaSort\"", view, StringComparison.Ordinal);
        Assert.Contains("<caption id=\"tenantTableCaption\"", view, StringComparison.Ordinal);
        Assert.Contains("role=\"search\"", view, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", view, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"mt-3\" aria-label=", view, StringComparison.Ordinal);
        Assert.Matches("<th scope=\"col\" class=\"text-end\"><span class=\"visually-hidden\">", view);
        // Sorting is plain links and filtering a GET form: keyboard-operable without script.
        Assert.DoesNotContain("<script", view, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusBadges_CarryTextAndHideTheirIcon()
    {
        var badge = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Shared", "_AdminStatusBadge.cshtml");

        Assert.Contains("aria-hidden=\"true\"", badge, StringComparison.Ordinal);
        Assert.Contains("<span>@L[Model.LabelKey]</span>", badge, StringComparison.Ordinal);
    }

    [Fact]
    public void Stylesheet_KeepsFocusVisible_IsResponsive_AndFlipsForRtl()
    {
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-admin-operations.css");
        var layout = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Shared", "_AdminLayout.cshtml");

        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("outline: 2px solid", css, StringComparison.Ordinal);
        Assert.DoesNotContain("outline: none", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 575.98px)", css, StringComparison.Ordinal);
        Assert.Contains("[dir=\"rtl\"] .wasla-admin-ops .wasla-admin-flip", css, StringComparison.Ordinal);
        Assert.Contains("var(--bs-", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("#[0-9a-fA-F]{3,6}\\b"), css);
        Assert.Contains("wasla-admin-operations.css", layout, StringComparison.Ordinal);

        var list = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Customers", "Index.cshtml");
        Assert.Contains("table-responsive", list, StringComparison.Ordinal);
        Assert.Contains("d-none d-md-table-cell", list, StringComparison.Ordinal);
        Assert.Contains("d-none d-xl-table-cell", list, StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_EveryUsedKeyExistsOnceInEveryCulture_WithMatchingPlaceholders()
    {
        var used = AdminSources()
            .SelectMany(source => UsedKey().Matches(source.Text).Select(m => m.Groups[1].Value))
            .Concat(Enum.GetValues<Wasla.Application.Admin.TenantOperationsGuidanceCode>().Select(AdminOperationsPresenter.GuidanceKey))
            .Where(key => key.StartsWith("Admin.Ops.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(used.Count > 150, $"Only {used.Count} keys found.");

        var english = Load(".en-US");
        foreach (var culture in Cultures)
        {
            var names = Names(culture);
            var values = Load(culture);
            foreach (var key in used)
            {
                Assert.True(names.Count(name => name == key) == 1, $"{key} appears {names.Count(name => name == key)} times in SharedResource{culture}.resx");
                Assert.False(string.IsNullOrWhiteSpace(values[key]), $"{key} is empty in SharedResource{culture}.resx");
                Assert.Equal(Placeholders(english[key]), Placeholders(values[key]));
            }

            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }

        foreach (var culture in new[] { ".en-US", ".ar-SA", ".ru-RU" })
            Assert.NotEqual(Load(".tr-TR")["Admin.Ops.OverviewTitle"], Load(culture)["Admin.Ops.OverviewTitle"]);
    }

    private static IEnumerable<(string Path, string Text)> AdminSources()
    {
        var root = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Areas", "Admin");
        foreach (var path in new[]
                 {
                     Path.Combine(root, "Views", "Dashboard", "Index.cshtml"),
                     Path.Combine(root, "Views", "Customers", "Index.cshtml"),
                     Path.Combine(root, "Views", "Customers", "Details.cshtml"),
                     Path.Combine(root, "Views", "Shared", "_AdminStatusBadge.cshtml"),
                     Path.Combine(root, "Views", "Shared", "_AdminLoadError.cshtml"),
                     Path.Combine(root, "Models", "TenantOperations", "AdminOperationsPresenter.cs"),
                     Path.Combine(root, "Models", "TenantOperations", "AdminTenantOperationsViewModels.cs"),
                     Path.Combine(root, "Controllers", "CustomersController.cs"),
                     Path.Combine(root, "Controllers", "DashboardController.cs")
                 })
            yield return (path, File.ReadAllText(path));
    }

    private static string Read(params string[] segments) => File.ReadAllText(TenantOperationsRulesTests.RepoFile(segments));

    private static List<string> Names(string culture) =>
        Document(culture).Root!.Elements("data").Select(e => e.Attribute("name")?.Value ?? string.Empty).ToList();

    private static Dictionary<string, string> Load(string culture) =>
        Document(culture).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    private static XDocument Document(string culture) =>
        XDocument.Load(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"));

    private static string Placeholders(string value) =>
        string.Join(",", PlaceholderPattern().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));

    [GeneratedRegex("\"(Admin\\.Ops\\.[A-Za-z0-9.]+)\"")]
    private static partial Regex UsedKey();

    [GeneratedRegex("\\{\\d+\\}")]
    private static partial Regex PlaceholderPattern();
}
