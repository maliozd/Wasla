using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Wasla.Application.Abstractions.Notifications;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Notifications;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.Models.Notifications;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-122: notification settings list the sounds from the web root file provider and never fail
/// because the host has no physical web root (for example the binary started outside its content root).
/// </summary>
public sealed class NotificationSettingsSoundResolutionTests
{
    private static readonly string[] DefaultUrls =
        ["/sounds/bell1.mp3", "/sounds/bell2.mp3", "/sounds/bell3.mp3", "/sounds/bell4.mp3", "/sounds/bell5.mp3", "/sounds/bell6.mp3"];

    [Fact]
    public async Task Current_WithNullWebRoot_ReturnsDefaultSoundList()
    {
        var controller = CreateController(new TestWebHostEnvironment { WebRootPath = null! });

        var sounds = await GetCurrentSoundsAsync(controller);

        AssertDefaultSounds(sounds);
    }

    [Fact]
    public async Task Get_WithNullWebRoot_RendersModalWithDefaultSoundList()
    {
        var controller = CreateController(new TestWebHostEnvironment { WebRootPath = null! });

        var result = Assert.IsType<PartialViewResult>(await controller.Get(TestContext.Current.CancellationToken));

        var model = Assert.IsType<NotificationSettingsViewModel>(result.Model);
        Assert.Equal(DefaultUrls, model.AvailableSounds.Select(s => s.Url));
    }

    [Fact]
    public async Task Current_WithMissingWebRootFolder_ReturnsDefaultSoundList()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wasla-was122-" + Guid.NewGuid().ToString("N"));
        var controller = CreateController(new TestWebHostEnvironment { WebRootPath = missing });

        var sounds = await GetCurrentSoundsAsync(controller);

        AssertDefaultSounds(sounds);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Current_WithWebRootWithoutSoundsFolder_ReturnsDefaultSoundList()
    {
        using var webRoot = new TempWebRoot();
        var controller = CreateController(webRoot.CreateEnvironment());

        var sounds = await GetCurrentSoundsAsync(controller);

        AssertDefaultSounds(sounds);
    }

    [Fact]
    public async Task Current_WithValidWebRoot_UsesTheFileExtensionOnDisk()
    {
        using var webRoot = new TempWebRoot();
        webRoot.AddSound("bell1.mp3");
        webRoot.AddSound("bell2.wav");
        webRoot.AddSound("bell3.ogg");
        var controller = CreateController(webRoot.CreateEnvironment());

        var sounds = await GetCurrentSoundsAsync(controller);

        Assert.Equal(
            ["/sounds/bell1.mp3", "/sounds/bell2.wav", "/sounds/bell3.ogg", "/sounds/bell4.mp3", "/sounds/bell5.mp3", "/sounds/bell6.mp3"],
            sounds.Select(s => s.Url));
    }

    [Fact]
    public async Task Current_WithRepositoryWebRoot_ListsTheShippedSounds()
    {
        var wwwroot = Admin.TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot");
        using var provider = new PhysicalFileProvider(wwwroot);
        var controller = CreateController(new TestWebHostEnvironment { WebRootPath = wwwroot, WebRootFileProvider = provider });

        var sounds = await GetCurrentSoundsAsync(controller);

        Assert.Equal(DefaultUrls, sounds.Select(s => s.Url));
        Assert.All(sounds, s => Assert.True(File.Exists(Path.Combine(wwwroot, "sounds", s.Url["/sounds/".Length..]))));
    }

    private static void AssertDefaultSounds(IReadOnlyList<SoundEntry> sounds)
    {
        Assert.Equal(NotificationSoundOptions.AllowedNames, sounds.Select(s => s.Name));
        Assert.Equal(NotificationSoundOptions.AllowedNames.Select(NotificationSoundOptions.GetLabel), sounds.Select(s => s.Label));
        Assert.Equal(DefaultUrls, sounds.Select(s => s.Url));
    }

    private static async Task<IReadOnlyList<SoundEntry>> GetCurrentSoundsAsync(NotificationSettingsController controller)
    {
        var result = Assert.IsType<OkObjectResult>(await controller.Current(TestContext.Current.CancellationToken));
        var json = JsonSerializer.SerializeToElement(result.Value);

        Assert.True(json.GetProperty("newOrderSoundEnabled").GetBoolean());
        return json.GetProperty("availableSounds").EnumerateArray()
            .Select(s => new SoundEntry(
                s.GetProperty("name").GetString()!,
                s.GetProperty("label").GetString()!,
                s.GetProperty("url").GetString()!))
            .ToList();
    }

    private static NotificationSettingsController CreateController(IWebHostEnvironment environment)
    {
        var controller = new NotificationSettingsController(
            new FixedCurrentTenantService(),
            new DefaultNotificationSettingsService(),
            new UpdateNotificationSettingsCommandValidator(),
            environment);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                    authenticationType: "Test"))
            }
        };

        return controller;
    }

    private sealed record SoundEntry(string Name, string Label, string Url);

    private sealed class TempWebRoot : IDisposable
    {
        private PhysicalFileProvider? _provider;

        public TempWebRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wasla-was122-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void AddSound(string fileName)
        {
            var sounds = System.IO.Path.Combine(Path, "sounds");
            Directory.CreateDirectory(sounds);
            File.WriteAllBytes(System.IO.Path.Combine(sounds, fileName), [0]);
        }

        public TestWebHostEnvironment CreateEnvironment()
        {
            _provider = new PhysicalFileProvider(Path);
            return new TestWebHostEnvironment { WebRootPath = Path, WebRootFileProvider = _provider };
        }

        public void Dispose()
        {
            _provider?.Dispose();
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FixedCurrentTenantService : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } =
            new(Guid.NewGuid(), "Tenant", "tenant", "tenant.wasla.local");
    }

    private sealed class DefaultNotificationSettingsService : IUserNotificationSettingsService
    {
        public Task<GetNotificationSettingsResult> GetAsync(Guid customerId, Guid userId, CancellationToken ct) =>
            Task.FromResult(new GetNotificationSettingsResult());

        public Task UpdateAsync(Guid customerId, Guid userId, UpdateNotificationSettingsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Wasla.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
