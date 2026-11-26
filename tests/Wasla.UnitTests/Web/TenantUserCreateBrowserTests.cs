using System.Globalization;
using System.Resources;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The tenant Create user form is sent once (WAS-249), in a real headless Chromium browser over
/// <see cref="TenantSessionWebHost"/> (real tenant resolution, cookie scheme, controllers, views and scripts). The
/// server's user service is wrapped to count the creates that reach it and to hold each one until the test lets it go,
/// so a second submission comes while the first is still on its way. Skips only when no Chromium browser is installed
/// (see <see cref="HeadlessChromium.BrowserVariable"/>) or Bootstrap cannot be loaded from its CDN.
/// </summary>
/// <remarks>
/// While a form post waits for its answer, the DevTools protocol holds script and input for the page until the new
/// document commits. So the first submission is real input from the test, and the page itself makes the second one
/// (the same button, or the form's implicit submission for Enter), records what it saw in <c>sessionStorage</c>, and
/// then reads the Users page, which tells the server it is done. The test reads the record once the new page has loaded.
/// </remarks>
[Trait("Category", "Browser")]
public sealed class TenantUserCreateBrowserTests : IAsyncLifetime
{
    private const string NewUserPassword = "Fake-new-user-1";
    private const string RecordKey = "wasla-test-create-submits";
    private static readonly ResourceManager Resources = new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // Where a real click lands on the submit button, and what the browser's own validation left.
    private const string State = """
        (() => {
          const form = document.querySelector('form[data-users-create]');
          const button = form.querySelector('button[type="submit"]');
          const r = button.getBoundingClientRect();
          const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
          return {
            busy: form.getAttribute('aria-busy'),
            disabled: button.getAttribute('aria-disabled'),
            emailFocused: document.activeElement === form.querySelector('[name="Email"]'),
            emailType: form.querySelector('[name="Email"]').type,
            x: r.left + r.width / 2, y: r.top + r.height / 2,
            hit: !!(hit && button.contains(hit)),
            dir: document.documentElement.getAttribute('dir')
          };
        })()
        """;

    private readonly CreateGate _gate = new();
    private TenantSessionWebHost _host = null!;
    private string _ownerEmail = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TenantPostLoginRedirectTests.StartHostAsync(services =>
        {
            services.AddScoped<TenantUserRoleService>();
            services.Replace(ServiceDescriptor.Scoped<ITenantUserRoleService>(provider =>
                new HeldCreates(provider.GetRequiredService<TenantUserRoleService>(), _gate)));
        });
        _ownerEmail = _host.SeedUser(_host.AlphaId, "owner@alpha.test", UserRole.Owner).Email;
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Release.TrySetResult();
        await _host.DisposeAsync();
    }

    private Uri Origin => new($"http://{TenantSessionWebHost.AlphaHost}:{_host.BaseAddress.Port}");

    /// <summary>
    /// A double submission: a real click on "Create user" or a real Enter in the Email field, then the same again while
    /// the first is held at the server. The second is cancelled: one create reaches the server, one user is stored, and
    /// the browser lands on the new user's page with the "user created" message. While the first is on its way the
    /// button keeps its name and the focus stays where it was; the button says it does nothing (aria-disabled, dimmed,
    /// progress cursor) and the form is busy. A submission the browser's own validation stops (an email that is not
    /// one) sends nothing and leaves the form usable.
    /// </summary>
    [Theory]
    [InlineData("tr-TR", 1280, false, "click")]
    [InlineData("ar-SA", 390, true, "enter")]
    public async Task ADoubleSubmission_SendsOneCreate_AndLandsOnTheNewUser(string culture, int width, bool mobile, string input)
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"new-user-{input}@alpha.test";
        await using var browser = await BrowserAsync(culture, width, mobile, ct);
        await SignInAsync(browser, ct);
        await GoAsync(browser, "/settings/users/create", ct);

        // An email the browser's validation refuses: nothing is sent, and the form stays usable.
        await FillAsync(browser, "not-an-email", ct);
        var before = await StateAsync(browser, ct);
        Assert.Equal("email", before.GetProperty("emailType").GetString());
        Assert.Equal(culture == "ar-SA" ? "rtl" : "ltr", before.GetProperty("dir").GetString());
        await SubmitAsync(browser, before, input, ct);
        var refused = await StateAsync(browser, ct);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("busy").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("disabled").ValueKind);

        // The double submission: the first from the test, the second from the page once the first is on its way.
        await FillAsync(browser, email, ct);
        await RecordSubmissionsAsync(browser, input, ct);
        await browser.EvaluateAsync<bool>("(() => { window.__waslaPreviousPage = true; return true; })()", ct);
        await SubmitAsync(browser, await StateAsync(browser, ct), input, ct);
        await _gate.FirstArrived.Task.WaitAsync(Wait, ct);
        await _gate.PageDone.Task.WaitAsync(Wait, ct);
        _gate.Release.SetResult();
        await browser.WaitForNavigationAsync("__waslaPreviousPage", ct);

        Assert.Equal(1, _gate.Calls);
        Assert.Equal(1, await StoredUsersAsync(email, ct));
        await WaitForPageAsync(browser, null, ct);
        Assert.Matches("^/settings/users/[0-9a-f-]{36}$", await browser.EvaluateAsync<string>("location.pathname", ct));
        Assert.Equal(Text("TenantUsers.UserCreated", culture),
            await browser.EvaluateAsync<string>("document.querySelector('.alert-success[role=\"alert\"]')?.textContent.trim() ?? ''", ct));

        var submits = JsonDocument.Parse(await browser.EvaluateAsync<string>($"sessionStorage.getItem('{RecordKey}') || '[]'", ct)).RootElement;
        Assert.Equal(2, submits.GetArrayLength());
        var first = submits[0];
        var second = submits[1];
        Assert.False(first.GetProperty("prevented").GetBoolean(), first.ToString());
        Assert.True(second.GetProperty("prevented").GetBoolean(), second.ToString());
        Assert.Equal(("true", "true"), (first.GetProperty("busy").GetString(), first.GetProperty("disabled").GetString()));
        Assert.Equal(Text("TenantUsers.CreateUser", culture), first.GetProperty("label").GetString());
        Assert.Equal(input == "click" ? "button" : "email", first.GetProperty("focus").GetString());
        Assert.Equal("progress", first.GetProperty("cursor").GetString());
        Assert.True(first.GetProperty("opacity").GetDouble() < 1, first.ToString());
        Assert.True(first.GetProperty("inside").GetBoolean(), first.ToString());
        Assert.True(first.GetProperty("overflow").GetDouble() <= 0, first.ToString());
    }

    private async Task<HeadlessChromium> BrowserAsync(string culture, int width, bool mobile, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser Create user checks.");

        // The tenant host name resolves to the in-process server; everything else (the CDN) resolves normally.
        var browser = await HeadlessChromium.StartAsync(
            executable,
            [$"--host-resolver-rules=MAP {TenantSessionWebHost.AlphaHost} 127.0.0.1"],
            ct);
        return await browser.SetUpAsync(async started =>
        {
            await started.SetCookieAsync(Origin, CookieRequestCultureProvider.DefaultCookieName,
                Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
            await started.SetViewportAsync(width, mobile ? 844 : 860, mobile, ct);
        });
    }

    private async Task SignInAsync(HeadlessChromium browser, CancellationToken ct)
    {
        await GoAsync(browser, "/auth/login", ct);
        await browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[action="/auth/login"]');
              form.querySelector('[name="Email"]').value = {{JsonSerializer.Serialize(_ownerEmail)}};
              form.querySelector('[name="Password"]').value = {{JsonSerializer.Serialize(TenantSessionWebHost.Password)}};
              form.querySelector('button[type="submit"]').click();
              return true;
            })()
            """, ct);
        await WaitForPageAsync(browser, "/dashboard", ct);
    }

    /// <summary>Fills the form (the values are test data; the subject is how the form is sent).</summary>
    private static Task FillAsync(HeadlessChromium browser, string email, CancellationToken ct) =>
        browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[data-users-create]');
              form.querySelector('[name="FullName"]').value = 'Ayşe Kaya';
              form.querySelector('[name="Email"]').value = {{JsonSerializer.Serialize(email)}};
              form.querySelector('[name="Password"]').value = {{JsonSerializer.Serialize(NewUserPassword)}};
              form.querySelector('[name="ConfirmPassword"]').value = {{JsonSerializer.Serialize(NewUserPassword)}};
              form.querySelector('button[type="submit"]').scrollIntoView({ block: 'center' });
              return true;
            })()
            """, ct);

    /// <summary>
    /// Records every submission of the form after the page's own handler has seen it (this listener is added later, on
    /// the same document). After the first, the page waits until the post is on its way, submits again as the test did
    /// (the button, or the form's implicit submission for Enter), and then reads the Users page so the server knows.
    /// </summary>
    private static Task RecordSubmissionsAsync(HeadlessChromium browser, string input, CancellationToken ct) =>
        browser.EvaluateAsync<bool>($$"""
            (() => {
              const form = document.querySelector('form[data-users-create]');
              const button = form.querySelector('button[type="submit"]');
              const email = form.querySelector('[name="Email"]');
              const records = [];
              document.addEventListener('submit', event => {
                if (event.target !== form) return;
                const r = button.getBoundingClientRect();
                const style = getComputedStyle(button);
                const focused = document.activeElement;
                records.push({
                  prevented: event.defaultPrevented,
                  busy: form.getAttribute('aria-busy'),
                  disabled: button.getAttribute('aria-disabled'),
                  label: button.textContent.trim(),
                  focus: focused === button ? 'button' : focused === email ? 'email' : String(focused && focused.tagName),
                  cursor: style.cursor,
                  opacity: Number(style.opacity),
                  inside: r.left >= 0 && r.right <= innerWidth,
                  overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth
                });
                sessionStorage.setItem({{JsonSerializer.Serialize(RecordKey)}}, JSON.stringify(records));
                if (records.length !== 1) return;
                setTimeout(() => {
                  if ({{JsonSerializer.Serialize(input)}} === 'click') button.click(); else form.requestSubmit();
                  fetch('/settings/users', { credentials: 'same-origin' });
                }, 300);
              });
              return true;
            })()
            """, ct);

    /// <summary>A real click on the submit button, or a real Enter in the Email field.</summary>
    private static async Task SubmitAsync(HeadlessChromium browser, JsonElement state, string input, CancellationToken ct)
    {
        if (input == "click")
        {
            Assert.True(state.GetProperty("hit").GetBoolean(), $"The submit button is covered: {state}");
            await browser.ClickAtAsync(state.GetProperty("x").GetDouble(), state.GetProperty("y").GetDouble(), ct);
            return;
        }

        if (!state.GetProperty("emailFocused").GetBoolean())
            await browser.EvaluateAsync<bool>("(() => { document.querySelector('form[data-users-create] [name=\"Email\"]').focus(); return true; })()", ct);
        await browser.PressKeyAsync("Enter", shift: false, ct);
    }

    private async Task<int> StoredUsersAsync(string email, CancellationToken ct)
    {
        await using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite(_host.Tenants.ConnectionStringFor(_host.AlphaId)).Options);
        return await db.AppUsers.CountAsync(u => u.Email == email, ct);
    }

    private async Task GoAsync(HeadlessChromium browser, string path, CancellationToken ct)
    {
        await browser.EvaluateAsync<bool>($"(() => {{ location.href = {JsonSerializer.Serialize(new Uri(Origin, path).ToString())}; return true; }})()", ct);
        await WaitForPageAsync(browser, path, ct);
    }

    private static async Task WaitForPageAsync(HeadlessChromium browser, string? expectedPath, CancellationToken ct)
    {
        var condition = expectedPath is null ? "true" : $"location.pathname === {JsonSerializer.Serialize(expectedPath)}";
        if (!await browser.WaitForAsync($"({condition}) && document.readyState === 'complete'", Wait, ct))
            Assert.Fail($"Expected {expectedPath}, the browser is at {await browser.EvaluateAsync<string>("location.pathname", ct)}.");
        if (!await browser.WaitForAsync("getComputedStyle(document.documentElement).getPropertyValue('--bs-blue').trim() !== ''", TimeSpan.FromSeconds(15), ct))
            Assert.Skip("Bootstrap did not load (its CDN is unreachable), so the tenant pages cannot be exercised.");
    }

    private static Task<JsonElement> StateAsync(HeadlessChromium browser, CancellationToken ct) =>
        browser.EvaluateAsync<JsonElement>(State, ct);

    private static string Text(string key, string culture) =>
        Resources.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? throw new InvalidOperationException($"No {culture} text for {key}.");

    /// <summary>
    /// Counts the creates that reach the server and holds each one until the test releases them. A Users page read after
    /// the first create arrived is the page saying it has made its second submission.
    /// </summary>
    private sealed class CreateGate
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource FirstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PageDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arrive()
        {
            Interlocked.Increment(ref _calls);
            FirstArrived.TrySetResult();
        }

        public void UsersRead()
        {
            if (FirstArrived.Task.IsCompleted)
                PageDone.TrySetResult();
        }
    }

    /// <summary>The real user service, with its creates counted and held by <see cref="CreateGate"/>.</summary>
    private sealed class HeldCreates(ITenantUserRoleService inner, CreateGate gate) : ITenantUserRoleService
    {
        public async Task<TenantUserMutationResult> CreateUserAsync(
            Guid tenantId, TenantUserActor actor, TenantUserCreateCommand command, CancellationToken ct)
        {
            gate.Arrive();
            await gate.Release.Task.WaitAsync(ct);
            return await inner.CreateUserAsync(tenantId, actor, command, ct);
        }

        public Task<IReadOnlyList<TenantUserSummaryDto>> ListUsersAsync(Guid tenantId, CancellationToken ct)
        {
            gate.UsersRead();
            return inner.ListUsersAsync(tenantId, ct);
        }

        public Task<TenantUserSummaryDto?> GetUserAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            inner.GetUserAsync(tenantId, userId, ct);

        public Task<TenantUserMutationResult> UpdateUserAsync(
            Guid tenantId, TenantUserActor actor, Guid userId, TenantUserUpdateCommand command, CancellationToken ct) =>
            inner.UpdateUserAsync(tenantId, actor, userId, command, ct);

        public Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
            Guid tenantId, TenantUserActor actor, Guid userId, UserRole role, CancellationToken ct) =>
            inner.ChangeRoleAsync(tenantId, actor, userId, role, ct);

        public Task<TenantUserRoleUpdateResult> SetActiveAsync(
            Guid tenantId, TenantUserActor actor, Guid userId, bool isActive, CancellationToken ct) =>
            inner.SetActiveAsync(tenantId, actor, userId, isActive, ct);

        public Task<TenantUserRoleUpdateResult> RemoveAsync(Guid tenantId, TenantUserActor actor, Guid userId, CancellationToken ct) =>
            inner.RemoveAsync(tenantId, actor, userId, ct);
    }
}
