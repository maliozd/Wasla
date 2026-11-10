using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-92 across a real Windows account boundary. A second, temporary local account plays the other user on a shared
/// machine: it creates the pipe and the locks first, under its own identity, and the test process (this user) launches
/// against them.
///
/// Creating a local account changes the machine, so these tests run only on GitHub-hosted runners (disposable,
/// elevated virtual machines) and skip everywhere else. The account gets a random name and password, is used through a
/// network logon token on this thread only (impersonation), and is deleted afterwards.
/// </summary>
public sealed class SetupInstanceCrossAccountTests
{
    private const string FakeLink =
        "wasla-printbridge://setup?server=https%3A%2F%2Fsushim.app.wasla.tr%2F&code=FAKE0cross0account0only0000000";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ForwardingToAnotherAccountsPipe_IsRejected_AndWritesNothing()
    {
        SkipUnlessDisposableRunner();
        using var other = TemporaryWindowsAccount.Create();
        var pipeName = UniquePipeName();

        // The other account pre-creates this user's pipe name with permissive access rules.
        using var squatter = other.Run(() =>
            NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, EveryoneMayConnect()));
        var squatterConnection = squatter.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var result = SetupInstanceChannel.TrySend(FakeLink, pipeName, ConnectTimeout);

        Assert.Equal(SetupForwardResult.Rejected, result);
        // The owner check runs after the connection is made, before anything is written; whatever connected sent no byte.
        if (await Task.WhenAny(squatterConnection, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)) == squatterConnection)
            Assert.Equal(0, await ReadByteCountAsync(squatter));
    }

    [Fact]
    public async Task Listener_DoesNotJoinAnotherAccountsPipe_AndReceivesNothingSentToIt()
    {
        SkipUnlessDisposableRunner();
        using var other = TemporaryWindowsAccount.Create();
        var pipeName = UniquePipeName();

        using var squatter = other.Run(() =>
            NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, EveryoneMayConnect()));

        var logs = new SetupInstanceChannelTests.CapturingLogger();
        var received = new List<string>();
        using var channel = new SetupInstanceChannel(logs, pipeName, TimeSpan.FromSeconds(2));
        channel.UriReceived += uri => { lock (received) received.Add(uri); };
        channel.StartListening();
        await logs.WaitForAsync("already in use by another process");

        // The other account sends a link to that name: it reaches only its own pipe. The pipe has no buffer, so the
        // squatter reads while the client writes.
        var squatterReceived = Task.Run(async () =>
        {
            await squatter.WaitForConnectionAsync(TestContext.Current.CancellationToken)
                .WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
            return await ReadByteCountAsync(squatter);
        });
        other.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);
            client.Write(Encoding.UTF8.GetBytes(FakeLink));
        });
        Assert.Equal(FakeLink.Length, await squatterReceived);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        lock (received)
            Assert.Empty(received);
    }

    [Fact]
    public void LocksCreatedByAnotherAccount_AreNeverTreatedAsFree()
    {
        SkipUnlessDisposableRunner();
        using var other = TemporaryWindowsAccount.Create();

        // Global names: the other account's logon is not in this session, and a startup lock held by anyone must stop
        // this launch from starting an engine.
        var id = Guid.NewGuid().ToString("N");
        var instanceName = $@"Global\WaslaPbTest.Instance.{id}";
        var engineName = $@"Global\WaslaPbTest.Engine.{id}";

        using (other.Run(() => new Mutex(initiallyOwned: false, engineName)))
        {
            var guard = SingleInstanceGuard.TryAcquire(instanceName, engineName, out var outcome);
            guard?.Dispose();

            Assert.Null(guard);
            Assert.Equal(SingleInstanceOutcome.EngineRunningElsewhere, outcome);
        }

        using (other.Run(() => new Mutex(initiallyOwned: false, instanceName)))
        {
            var guard = SingleInstanceGuard.TryAcquire(instanceName, engineName, out var outcome);
            guard?.Dispose();

            Assert.Null(guard);
            Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, outcome);
        }
    }

    private static void SkipUnlessDisposableRunner()
    {
        var onHostedRunner =
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);
        if (!onHostedRunner)
            Assert.Skip("Creates a temporary local Windows account; runs only on GitHub-hosted runners.");
    }

    private static string UniquePipeName() => $"WaslaPbTest.Setup.{Guid.NewGuid():N}";

    private static PipeSecurity EveryoneMayConnect()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

    private static async Task<int> ReadByteCountAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken).WaitAsync(ConnectTimeout, TestContext.Current.CancellationToken);
        return (int)buffer.Length;
    }

    /// <summary>A local standard-user account that exists only for one test.</summary>
    private sealed class TemporaryWindowsAccount : IDisposable
    {
        private const uint UserPrivUser = 1;
        private const uint UfScript = 0x0001;
        private const uint UfDontExpirePasswd = 0x10000;
        private const int Logon32LogonInteractive = 2;
        private const int Logon32LogonNetwork = 3;
        private const int Logon32ProviderDefault = 0;

        private readonly SafeAccessTokenHandle _token;

        private TemporaryWindowsAccount(string name, SecurityIdentifier sid, SafeAccessTokenHandle token)
        {
            Name = name;
            Sid = sid;
            _token = token;
        }

        public string Name { get; }

        public SecurityIdentifier Sid { get; }

        public static TemporaryWindowsAccount Create()
        {
            var name = "wpbipc" + RandomNumberGenerator.GetHexString(8, lowercase: true);
            var password = "Wp9!" + RandomNumberGenerator.GetString(
                "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 28);

            var info = new UserInfo1
            {
                Name = name,
                Password = password,
                Privilege = UserPrivUser,
                Comment = "Temporary account of a Wasla Print Bridge test; deleted by the test.",
                Flags = UfScript | UfDontExpirePasswd,
            };
            var status = NetUserAdd(null, 1, ref info, out var parameterError);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"NetUserAdd failed with status {status} (parameter {parameterError}). These tests need an elevated, disposable machine.");
            }

            try
            {
                var token = LogOn(name, password);
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                var sid = identity.User ?? throw new InvalidOperationException("The temporary account has no SID.");
                using var current = WindowsIdentity.GetCurrent();
                Assert.NotEqual(current.User, sid);
                return new TemporaryWindowsAccount(name, sid, token);
            }
            catch
            {
                _ = NetUserDel(null, name);
                throw;
            }
        }

        /// <summary>Runs <paramref name="action"/> on this thread as the temporary account.</summary>
        public T Run<T>(Func<T> action) =>
            WindowsIdentity.RunImpersonated(_token, () =>
            {
                using var acting = WindowsIdentity.GetCurrent();
                Assert.Equal(Sid, acting.User);
                Assert.Equal(TokenImpersonationLevel.Impersonation, acting.ImpersonationLevel);
                return action();
            });

        public void Run(Action action) => Run(() =>
        {
            action();
            return 0;
        });

        public void Dispose()
        {
            _token.Dispose();
            _ = NetUserDel(null, Name);
        }

        private static SafeAccessTokenHandle LogOn(string name, string password)
        {
            var errors = new List<string>();
            foreach (var logonType in new[] { Logon32LogonNetwork, Logon32LogonInteractive })
            {
                if (LogonUserW(name, ".", password, logonType, Logon32ProviderDefault, out var handle))
                    return new SafeAccessTokenHandle(handle);
                errors.Add($"type {logonType}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            throw new InvalidOperationException("LogonUser failed for the temporary account: " + string.Join("; ", errors));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct UserInfo1
        {
            public string Name;
            public string Password;
            public uint PasswordAge;
            public uint Privilege;
            public string? HomeDirectory;
            public string? Comment;
            public uint Flags;
            public string? ScriptPath;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserAdd(string? serverName, uint level, ref UserInfo1 buffer, out uint parameterError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserDel(string? serverName, string userName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LogonUserW(
            string userName, string domain, string password, int logonType, int logonProvider, out IntPtr token);
    }
}
