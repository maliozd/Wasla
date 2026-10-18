using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Branding;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Branding;

/// <summary>
/// WAS-127: the restaurant logo is stored in each tenant's own database (SQLite here, one database per tenant as in
/// production), versioned by its content, replaced or removed as a whole, and carries the background style the Owner
/// chose for the Live Screen header, which changes without touching the image.
/// </summary>
public sealed class RestaurantLogoServiceTests : IDisposable
{
    private static readonly Guid Alpha = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000127");
    private static readonly Guid Beta = Guid.Parse("b2b2b2b2-0000-4000-8000-000000000127");

    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly RestaurantLogoService _service;

    public RestaurantLogoServiceTests()
    {
        _tenants.CreateTenantDatabase(Alpha);
        _tenants.CreateTenantDatabase(Beta);
        _service = new RestaurantLogoService(_tenants, NullLogger<RestaurantLogoService>.Instance);
    }

    public void Dispose() => _tenants.Dispose();

    private int LogoRows(Guid tenantId)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_tenants.ConnectionStringFor(tenantId)).Options);
        return db.RestaurantLogos.Count();
    }

    private async Task<string?> VersionAsync(Guid tenantId) =>
        (await _service.GetCurrentAsync(tenantId, TestContext.Current.CancellationToken))?.Version;

    private async Task<RestaurantLogoBackground?> BackgroundAsync(Guid tenantId) =>
        (await _service.GetCurrentAsync(tenantId, TestContext.Current.CancellationToken))?.Background;

    [Fact]
    public async Task ANewLogo_IsThemeAware()
    {
        var ct = TestContext.Current.CancellationToken;

        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);

        Assert.Equal(RestaurantLogoBackground.ThemeAware, await BackgroundAsync(Alpha));
    }

    [Theory]
    [InlineData(RestaurantLogoBackground.Transparent)]
    [InlineData(RestaurantLogoBackground.Light)]
    [InlineData(RestaurantLogoBackground.Dark)]
    [InlineData(RestaurantLogoBackground.ThemeAware)]
    public async Task TheBackground_IsStored_WithoutTouchingTheImage(RestaurantLogoBackground background)
    {
        var ct = TestContext.Current.CancellationToken;
        var png = TestImages.Png(48, 48);
        var saved = await _service.SaveAsync(Alpha, png, ct);

        Assert.True(await _service.SetBackgroundAsync(Alpha, background, ct));

        Assert.Equal(background, await BackgroundAsync(Alpha));
        var logo = await _service.GetAsync(Alpha, ct);
        Assert.Equal(png, logo!.Content);
        Assert.Equal(saved.Version, logo.Version);
    }

    [Fact]
    public async Task ReplacingTheLogo_KeepsItsBackground_AndRemovingItResetsTheNextOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);
        await _service.SetBackgroundAsync(Alpha, RestaurantLogoBackground.Dark, ct);

        await _service.SaveAsync(Alpha, TestImages.Png(48, 48, red: 1, green: 2, blue: 3), ct);
        Assert.Equal(RestaurantLogoBackground.Dark, await BackgroundAsync(Alpha));

        await _service.RemoveAsync(Alpha, ct);
        Assert.Null(await BackgroundAsync(Alpha));
        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);
        Assert.Equal(RestaurantLogoBackground.ThemeAware, await BackgroundAsync(Alpha));
    }

    [Fact]
    public async Task WithoutALogo_TheBackgroundIsNotStored()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await _service.SetBackgroundAsync(Alpha, RestaurantLogoBackground.Light, ct));

        Assert.Equal(0, LogoRows(Alpha));
    }

    [Fact]
    public async Task AnUndefinedBackground_IsRefused_AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);
        await _service.SetBackgroundAsync(Alpha, RestaurantLogoBackground.Light, ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.SetBackgroundAsync(Alpha, (RestaurantLogoBackground)99, ct));

        Assert.Equal(RestaurantLogoBackground.Light, await BackgroundAsync(Alpha));
    }

    [Fact]
    public async Task EachTenantHasItsOwnBackground()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);
        await _service.SaveAsync(Beta, TestImages.Png(48, 48), ct);

        await _service.SetBackgroundAsync(Beta, RestaurantLogoBackground.Dark, ct);

        Assert.Equal(RestaurantLogoBackground.ThemeAware, await BackgroundAsync(Alpha));
        Assert.Equal(RestaurantLogoBackground.Dark, await BackgroundAsync(Beta));
    }

    [Fact]
    public async Task WithoutALogo_NothingIsReturned()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await VersionAsync(Alpha));
        Assert.Null(await _service.GetAsync(Alpha, ct));
        Assert.False(await _service.RemoveAsync(Alpha, ct));
    }

    [Fact]
    public async Task ASavedLogo_IsReturnedWithTheDetectedTypeAndAContentVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var png = TestImages.Png(48, 48);

        var saved = await _service.SaveAsync(Alpha, png, ct);

        Assert.True(saved.Succeeded);
        Assert.Matches("^[0-9a-f]{32}$", saved.Version);
        Assert.Equal(saved.Version, await VersionAsync(Alpha));
        var logo = await _service.GetAsync(Alpha, ct);
        Assert.NotNull(logo);
        Assert.Equal(png, logo.Content);
        Assert.Equal(RestaurantLogoImage.Png, logo.ContentType);
        Assert.Equal(saved.Version, logo.Version);
    }

    [Fact]
    public async Task ReplacingTheLogo_ChangesTheVersion_AndKeepsOneLogo()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);

        var second = await _service.SaveAsync(Alpha, TestImages.Png(48, 48, red: 0x10, green: 0x20, blue: 0x30), ct);
        var again = await _service.SaveAsync(Alpha, TestImages.Png(48, 48, red: 0x10, green: 0x20, blue: 0x30), ct);

        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(second.Version, again.Version);
        Assert.Equal(second.Version, await VersionAsync(Alpha));
        Assert.Equal(1, LogoRows(Alpha));
    }

    [Fact]
    public async Task ARefusedImage_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var saved = await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);

        var refused = await _service.SaveAsync(Alpha, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), ct);

        Assert.Equal(RestaurantLogoError.UnsupportedFormat, refused.Error);
        Assert.Null(refused.Version);
        Assert.Equal(saved.Version, await VersionAsync(Alpha));
    }

    [Fact]
    public async Task RemovingTheLogo_LeavesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);

        Assert.True(await _service.RemoveAsync(Alpha, ct));

        Assert.Null(await VersionAsync(Alpha));
        Assert.Null(await _service.GetAsync(Alpha, ct));
    }

    [Fact]
    public async Task EachTenantHasItsOwnLogo()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = await _service.SaveAsync(Alpha, TestImages.Png(48, 48), ct);

        Assert.Null(await _service.GetAsync(Beta, ct));
        Assert.Null(await VersionAsync(Beta));
        Assert.False(await _service.RemoveAsync(Beta, ct));
        Assert.Equal(alpha.Version, await VersionAsync(Alpha));

        var beta = await _service.SaveAsync(Beta, TestImages.Png(48, 48, red: 0, green: 0, blue: 0), ct);
        Assert.True(await _service.RemoveAsync(Alpha, ct));
        Assert.Equal(beta.Version, await VersionAsync(Beta));
    }
}
