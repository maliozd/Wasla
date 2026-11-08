using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;
using static Wasla.PrintBridge.Tests.DeviceApi;
using ReplyKind = Wasla.PrintBridge.Services.WaslaPrintBridgeClient.SetupTransferReplyKind;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-191: an answer that is not a success is read only to classify it. What it says reaches no log at any level and is
/// not kept on the exception that callers log, because a server or proxy may echo what the request carried. The answers
/// here echo the request's token and body around fake secrets in the body, the <c>X-PrintBridge-Error-Code</c> header and
/// the <c>error</c> field. Only a code the Wasla server sends is logged, by name; classification, token handling and
/// recovery are unchanged. The client comes from the app's own registration and talks over real loopback sockets; the
/// engine tests run the real <see cref="PrintBridgeRuntime"/> on it with a recording printer.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeDeviceApiResponseLoggingTests : IDisposable
{
    private const string BodySecret = "body-secret-sentinel";
    private const string HeaderCodeSecret = "header-code-secret-sentinel";
    private const string FieldCodeSecret = "field-code-secret-sentinel";
    private const string SetupCodeSecret = "setup-code-secret-sentinel";
    private static readonly int[] ErrorStatuses = [400, 401, 403, 404, 409, 500];

    private static readonly string[] Secrets =
    [
        BodySecret, HeaderCodeSecret, FieldCodeSecret, SetupCodeSecret,
        FakeToken, CandidateToken, FailureSentinel, DeviceNameSentinel
    ];

    // Each engine test disposes its engine (which waits for the polling loop) before this root is released.
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-device-response-log-tests");

    /// <summary>Where an error answer carries its error code, and so what it echoes.</summary>
    public enum Shape
    {
        /// <summary>The <c>X-PrintBridge-Error-Code</c> header and a JSON body with an <c>error</c> field.</summary>
        HeaderAndField,

        /// <summary>A JSON body with an <c>error</c> field and no header.</summary>
        Field,

        /// <summary>An HTML page, as a proxy answers: no error code at all.</summary>
        Text
    }

    public void Dispose() => _dataRoot.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, string, Shape> EveryErrorOfEveryCall()
    {
        var data = new TheoryData<int, string, Shape>();
        foreach (var status in ErrorStatuses)
        {
            foreach (var call in Calls.Keys)
            {
                foreach (var shape in Enum.GetValues<Shape>())
                    data.Add(status, call, shape);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryErrorOfEveryCall))]
    public async Task AnErrorAnswer_IsClassifiedAsBefore_ButWhatItSaysReachesNoLog(int status, string callName, Shape shape)
    {
        var call = Calls[callName];
        using var server = new LoopbackSetupServer(new EchoingErrors(new ErrorAnswer(status, shape)));
        var logs = new CapturedLogs();
        using var app = DeviceApiApp.Create(server.Url, logs);

        var error = await Assert.ThrowsAsync<PrintBridgeConnectionException>(() => call.Send(app.Client, server.Url));

        // The codes in these answers are not ones the server sends, so the status alone decides, as before.
        var (issue, key) = status switch
        {
            401 or 403 => (PrintBridgeRuntimeIssueCode.ReconnectRequired, "RuntimeIssue.ReconnectRequired.Detail"),
            404 => (PrintBridgeRuntimeIssueCode.EndpointNotFound, "Connection.EndpointNotFound"),
            >= 500 => (PrintBridgeRuntimeIssueCode.RequestFailed, "Connection.ServerUnavailable"),
            _ => (PrintBridgeRuntimeIssueCode.RequestFailed, "Connection.RequestFailed")
        };
        Assert.Equal((status, issue, key), (error.StatusCode, error.IssueCode, error.UserMessageKey));
        Assert.Equal(status is 401 or 403, new PrintBridgeRuntimeIssue(error.IssueCode, error.UserMessageKey).ShouldClearToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((call.Method, call.Token), (request.Method, request.Token));
        if (call.BodySentinel is not null)
            Assert.Contains(call.BodySentinel, request.Body, StringComparison.Ordinal);

        // Whoever logs the exception writes nothing the answer said.
        Assert.Empty(error.Data);
        AssertSaysNothingOfTheAnswer(error.ToString());
        AssertSaysNothingOfTheAnswer(logs.Text());

        // What is logged instead: the status, the endpoint, the issue, and that the code was not one the server sends.
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Print Bridge API failed.", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(status, warning.Properties["StatusCode"]);
        Assert.StartsWith(call.Path, warning.Properties["Path"] as string, StringComparison.Ordinal);
        Assert.Equal(issue, warning.Properties["Issue"]);
        Assert.Equal(shape == Shape.Text ? null : "unrecognized", warning.Properties["ErrorCode"]);
    }

    [Theory]
    [InlineData(401, "device_removed", Shape.HeaderAndField, PrintBridgeRuntimeIssueCode.ReconnectRequired, "RuntimeIssue.ReconnectRequired.TokenRevoked.Detail", "device_removed")]
    [InlineData(401, "Device_Auth_Invalid", Shape.Field, PrintBridgeRuntimeIssueCode.ReconnectRequired, "RuntimeIssue.ReconnectRequired.TokenRevoked.Detail", "device_auth_invalid")]
    [InlineData(401, "tenant_inactive", Shape.HeaderAndField, PrintBridgeRuntimeIssueCode.ReconnectRequired, "RuntimeIssue.ReconnectRequired.Detail", "tenant_inactive")]
    [InlineData(403, "device_disabled", Shape.Field, PrintBridgeRuntimeIssueCode.DisabledByAdmin, "RuntimeIssue.DisabledByAdmin.Detail", "device_disabled")]
    [InlineData(409, "installation_mismatch", Shape.HeaderAndField, PrintBridgeRuntimeIssueCode.DuplicateInstallation, "RuntimeIssue.DuplicateInstallation.Detail", "installation_mismatch")]
    public async Task AKnownErrorCode_StillDecides_AndIsLoggedByName_WithoutTheRestOfTheAnswer(
        int status,
        string code,
        Shape shape,
        PrintBridgeRuntimeIssueCode issue,
        string key,
        string loggedCode)
    {
        using var server = new LoopbackSetupServer(new EchoingErrors(new ErrorAnswer(status, shape, code)));
        var logs = new CapturedLogs();
        using var app = DeviceApiApp.Create(server.Url, logs);

        var error = await Assert.ThrowsAsync<PrintBridgeConnectionException>(() => app.Client.TestHealthAsync(Ct));

        Assert.Equal((issue, key), (error.IssueCode, error.UserMessageKey));
        Assert.Empty(error.Data);
        AssertSaysNothingOfTheAnswer(error.ToString());
        AssertSaysNothingOfTheAnswer(logs.Text());
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Print Bridge API failed.", StringComparison.Ordinal));
        Assert.Equal(loggedCode, warning.Properties["ErrorCode"]);
        Assert.Equal(issue, warning.Properties["Issue"]);
    }

    /// <summary>
    /// The engine logs each of these failures with its exception. Those lines, and every other line at any level, say
    /// nothing the answer said, and the engine reacts as before: a rejected token is cleared, a claim that failed is never
    /// printed, a "printed" report is repeated and never turned into a failure.
    /// </summary>
    [Theory]
    [InlineData("poll", 500)]
    [InlineData("poll", 401)]
    [InlineData("claim", 409)]
    [InlineData("printed", 500)]
    [InlineData("failed", 500)]
    public async Task TheEngineLogsAFailedRequest_WithoutWhatTheAnswerSaid_AndReactsAsBefore(string step, int status)
    {
        var failingPath = step switch
        {
            "poll" => PendingPath,
            "claim" => JobPath("mark-printing"),
            "printed" => JobPath("mark-printed"),
            _ => JobPath("mark-failed")
        };
        var api = new JobContractServer();
        api.Add(JobId);
        using var server = new LoopbackSetupServer(
            new EchoingErrors(new ErrorAnswer(status, Shape.HeaderAndField), path => path == failingPath, api));
        var logs = new CapturedLogs();
        using var engine = DeviceApiEngine.Create(server.Url, logs);
        engine.Runtime.PrintedReportRetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        if (step == "failed")
            engine.Printer.Failure = new InvalidOperationException("The test printer refused the job.");
        var attempts = step == "printed" ? 1 + engine.Runtime.PrintedReportRetryDelays.Count : 1;

        engine.Runtime.Start();
        await WaitUntilAsync(() => server.Count(failingPath) >= attempts && logs.Entries.Any(IsALoggedRequestFailure));
        if (status != 401)
        {
            var polls = server.Count(PendingPath);
            await WaitUntilAsync(() => server.Count(PendingPath) >= polls + 1);
        }

        await engine.Runtime.StopAsync();

        AssertSaysNothingOfTheAnswer(logs.Text());
        switch (step)
        {
            case "poll" when status == 401:
                Assert.True(string.IsNullOrEmpty(engine.Holder.Connection.AgentToken));
                Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, engine.Runtime.GetStatus().LastIssue?.Code);
                Assert.Empty(engine.Printer.Calls);
                break;
            case "poll" or "claim":
                // Polls and refused claims repeat on every cycle; nothing is printed and the token is kept.
                Assert.Equal(FakeToken, engine.Holder.Connection.AgentToken);
                Assert.Empty(engine.Printer.Calls);
                Assert.Equal("Pending", api.State(JobId));
                break;
            case "printed":
                Assert.Single(engine.Printer.Calls);
                Assert.Equal(attempts, server.Count(failingPath));
                Assert.Equal(0, server.Count(JobPath("mark-failed")));
                Assert.Equal(LocalPrintJobStatus.Printed, Assert.Single(engine.Runtime.GetStatus().RecentJobs).Status);
                Assert.Equal("Printing", api.State(JobId));
                break;
            case "failed":
                Assert.Empty(engine.Printer.Calls);
                Assert.Equal(1, server.Count(failingPath));
                Assert.Equal("Printing", api.State(JobId));
                break;
        }

        static bool IsALoggedRequestFailure(CapturedLogEntry entry) =>
            entry.Category == typeof(PrintBridgeRuntime).FullName && entry.Exception is PrintBridgeConnectionException;
    }

    [Theory]
    [InlineData(409, "installation_already_registered", "Auto.InstallationAlreadyRegistered", "installation_already_registered")]
    [InlineData(409, "installation_registered_to_another_tenant", "Auto.InstallationRegisteredToAnotherRestaurant", "installation_registered_to_another_tenant")]
    [InlineData(400, null, null, "server_rejection")]
    public async Task ARejectedSetupExchange_IsClassifiedAsBefore_ButItsBodyReachesNoLog(
        int status,
        string? code,
        string? exceptionKey,
        string failureKind)
    {
        using var server = new LoopbackSetupServer(new EchoingErrors(new ErrorAnswer(status, code is null ? Shape.Text : Shape.Field, code)));
        var logs = new CapturedLogs();
        using var app = DeviceApiApp.Create(server.Url, logs);

        if (exceptionKey is null)
        {
            Assert.Null(await app.Client.ExchangeSetupAsync(server.Url, SetupCodeSecret, Ct));
        }
        else
        {
            var error = await Assert.ThrowsAsync<LocalizedApplicationException>(
                () => app.Client.ExchangeSetupAsync(server.Url, SetupCodeSecret, Ct));
            Assert.Equal(exceptionKey, error.ResourceKey);
        }

        // The answer echoed the setup code the request carried.
        Assert.Contains(SetupCodeSecret, Assert.Single(server.Requests).Body, StringComparison.Ordinal);
        AssertSaysNothingOfTheAnswer(logs.Text());
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Print Bridge setup exchange rejected by server.", StringComparison.Ordinal));
        Assert.Equal(failureKind, warning.Properties["FailureKind"]);
    }

    [Theory]
    [InlineData(null, "unrecognized", ReplyKind.InvalidOrExpired)]
    [InlineData("printing_in_progress", "printing_in_progress", ReplyKind.PrintingInProgress)]
    public async Task ARefusedMove_IsClassifiedAsBefore_AndOnlyAKnownErrorCodeIsLogged(string? code, string loggedCode, ReplyKind expected)
    {
        using var server = new LoopbackSetupServer(new EchoingErrors(new ErrorAnswer(409, Shape.HeaderAndField, code)));
        var logs = new CapturedLogs();
        using var app = DeviceApiApp.Create(server.Url, logs);

        var reply = await app.Client.PreviewSetupTransferAsync(
            new WaslaOptions { ServerUrl = server.Url, AgentToken = FakeToken },
            Guid.NewGuid().ToString("D"),
            "https://wasla.example",
            SetupCodeSecret,
            Ct);

        Assert.Equal(expected, reply.Kind);
        Assert.Equal(FakeToken, Assert.Single(server.Requests).Token);
        AssertSaysNothingOfTheAnswer(logs.Text());
        var warning = Assert.Single(logs.Entries, entry => entry.Message.StartsWith("Print Bridge transfer request refused.", StringComparison.Ordinal));
        Assert.Equal(loggedCode, warning.Properties["ErrorCode"]);
    }

    private static void AssertSaysNothingOfTheAnswer(string text)
    {
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error answer that echoes the request, as a misbehaving server or proxy might: the token in the error code, the
    /// token and the request body in the body, around fake secrets. <paramref name="Code"/>, when given, is the error code
    /// in the header and the field instead.
    /// </summary>
    private sealed record ErrorAnswer(int Status, Shape Shape, string? Code = null)
    {
        public HttpResponseMessage Create(string token, string requestBody)
        {
            var echo = $"{token} {requestBody}";
            var response = new HttpResponseMessage((HttpStatusCode)Status);
            if (Shape == Shape.Text)
            {
                response.Content = new StringContent($"<html><body>{BodySecret} {echo}</body></html>", Encoding.UTF8, "text/html");
                return response;
            }

            if (Shape == Shape.HeaderAndField)
                response.Headers.TryAddWithoutValidation("X-PrintBridge-Error-Code", Code ?? $"{HeaderCodeSecret} {token}");

            response.Content = new StringContent(
                JsonSerializer.Serialize(new { error = Code ?? $"{FieldCodeSecret} {token}", message = $"{BodySecret} {echo}" }),
                Encoding.UTF8,
                "application/json");
            return response;
        }
    }

    /// <summary>
    /// Answers the requests <c>fails</c> picks (every request when it is null) with <c>answer</c>, and forwards the rest
    /// to <c>next</c>.
    /// </summary>
    private sealed class EchoingErrors(ErrorAnswer answer, Func<string, bool>? fails = null, HttpMessageHandler? next = null)
        : HttpMessageHandler
    {
        private readonly HttpMessageInvoker? _next = next is null ? null : new HttpMessageInvoker(next, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (_next is not null && fails is not null && !fails(path))
                return await _next.SendAsync(request, cancellationToken);

            var token = request.Headers.TryGetValues("X-PrintBridge-Token", out var values) ? values.First() : string.Empty;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer.Create(token, body);
        }
    }
}
