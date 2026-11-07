using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The SMS sender modes of owner phone verification (WAS-192), configured as Program.cs reads them
/// (<c>Wasla:Sms:Mode</c>): without a sender the signup fails closed, the fake sender exists only in Development, and its
/// codes are readable only through the Development fake outbox. Each test serves the real signup over its own in-memory
/// CentralDb; nothing is sent.
/// </summary>
public sealed class SignupSmsModeTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-sms-keys-").FullName;
    private SignupWebHost? _host;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WithoutASender_ProductionFailsClosed_AndNoRegistrationCanBeCreated()
    {
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, Environments.Production, fakeSms: false);
        using var client = new SignupDraftClient(_host);
        await client.SaveSectionsAsync();

        var state = await SignupDraftClient.AssertOkAsync(client.GetDraftAsync());
        var send = await client.SendCodeAsync();
        var submit = await client.SubmitAsync();
        var outbox = await client.Browser.GetAsync("/signup/draft/phone/fake-outbox");

        Assert.Equal("unavailable", state.Json.GetProperty("phone").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.Status);
        Assert.Equal("Signup.Phone.Unavailable", send.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, submit.Status);
        Assert.Equal("Signup.Phone.NotVerified", submit.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, outbox.Status);
        Assert.Null(_host.FakeSms);
        await using var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);
        Assert.Equal(0, await db.PendingRegistrations.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Production", "Fake")]
    [InlineData("Staging", "Fake")]
    [InlineData("Production", "Twilio")]
    [InlineData("Development", "Real")]
    public async Task AFakeSenderOutsideDevelopment_OrAnUnknownMode_RefusesToStart(string environment, string mode)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SignupWebHost.StartAsync(
            _connection,
            _keyRing,
            environment,
            configuration: new Dictionary<string, string?> { ["Wasla:Sms:Mode"] = mode },
            fakeSms: false));

        Assert.Contains("Wasla:Sms:Mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InDevelopment_TheFakeModeShowsTheCodeOnlyInTheFakeOutbox()
    {
        _host = await SignupWebHost.StartAsync(
            _connection,
            _keyRing,
            Environments.Development,
            configuration: new Dictionary<string, string?> { ["Wasla:Sms:Mode"] = "Fake" },
            fakeSms: false);
        using var client = new SignupDraftClient(_host);
        await client.SaveSectionsAsync();
        var sent = await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());

        var outbox = await SignupDraftClient.AssertOkAsync(client.Browser.GetAsync("/signup/draft/phone/fake-outbox"));

        var message = Assert.Single(outbox.Json.GetProperty("messages").EnumerateArray());
        var code = message.GetProperty("code").GetString()!;
        Assert.Equal(_host.FakeSms!.LatestCodeTo(client.MobileE164), code);
        Assert.DoesNotContain(code, sent.Body, StringComparison.Ordinal);
        Assert.Contains("no-store", outbox.CacheControl, StringComparison.OrdinalIgnoreCase);
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(code));

        // Another browser has no draft, so it sees no code.
        using var stranger = new SignupDraftClient(_host);
        Assert.Equal(HttpStatusCode.Conflict, (await stranger.Browser.GetAsync("/signup/draft/phone/fake-outbox")).Status);
    }
}
