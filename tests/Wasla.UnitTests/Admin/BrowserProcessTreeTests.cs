namespace Wasla.UnitTests.Admin;

/// <summary>
/// Which processes a headless browser's cleanup may terminate (WAS-73): only the started process and its descendants,
/// identified by parent process id and creation time, never by name. Process snapshots and opened handles are
/// scripted, so process-id reuse and stale parent ids, which a real machine shows only under load, are reproduced
/// exactly.
/// </summary>
public sealed class BrowserProcessTreeTests
{
    private const long RootCreated = 1_000;

    [Fact]
    public void Refresh_TracksChildrenAndGrandchildren_AndNoOtherProcess()
    {
        var table = new ScriptedTable(
        [
            new ProcessEntry(100, 4, "msedge.exe"),
            new ProcessEntry(101, 100, "msedge.exe"),
            new ProcessEntry(102, 101, "msedge.exe"),
            new ProcessEntry(200, 4, "msedge.exe"),        // the user's Edge
            new ProcessEntry(201, 200, "msedge.exe"),
            new ProcessEntry(300, 77, "msedgewebview2.exe") // a WebView2 host
        ]);
        table.Processes[101] = new FakeTracked(101, RootCreated);
        table.Processes[102] = new FakeTracked(102, RootCreated + 5);
        var tree = table.Tree();

        var tracked = tree.Refresh();

        Assert.Equal([100, 101, 102], tracked.Select(p => p.Id));
        Assert.DoesNotContain(200, table.Opened);
        Assert.DoesNotContain(201, table.Opened);
        Assert.DoesNotContain(300, table.Opened);
    }

    [Fact]
    public void Refresh_IgnoresAProcessOlderThanItsSupposedParent()
    {
        // Process 101 was created before the browser and inherited its parent id from an earlier process with id 100.
        var table = new ScriptedTable([new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(101, 100, "msedge.exe")]);
        var older = table.Processes[101] = new FakeTracked(101, RootCreated - 1);

        var tracked = table.Tree().Refresh();

        Assert.Equal([100], tracked.Select(p => p.Id));
        Assert.True(older.Disposed);
    }

    [Fact]
    public async Task AProcessIdReusedBeforeItWasOpened_IsNotTracked_AndNeverTerminated()
    {
        // The snapshot lists the browser's child 501, which exits; before it is opened, id 501 passes to another
        // browser's new renderer (created later, so its creation time alone would pass). Opened, the handle pins that
        // renderer, and the second snapshot shows its real parent.
        var table = new ScriptedTable(
            [new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(501, 100, "msedge.exe")],
            [new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(501, 900, "msedge.exe")]);
        var reused = table.Processes[501] = new FakeTracked(501, RootCreated + 50);
        var tree = table.Tree();

        await new HeadlessBrowserCleanup(tree, BrowserProfile.Create(), () => TimeSpan.Zero, _ => Task.CompletedTask).RunAsync();

        Assert.Contains(501, table.Opened);
        Assert.True(reused.Disposed);
        Assert.Equal(0, reused.Terminations);
        Assert.True(table.Root.HasExited);
    }

    [Fact]
    public void Refresh_KeepsTrackingAChild_AfterItsParentExited()
    {
        var table = new ScriptedTable([new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(101, 100, "msedge.exe")]);
        table.Processes[101] = new FakeTracked(101, RootCreated + 1);
        var tree = table.Tree();
        tree.Refresh();

        table.Root.Exited = true;
        table.Script([new ProcessEntry(101, 100, "msedge.exe")]);

        Assert.Equal([100, 101], tree.Refresh().Select(p => p.Id));
    }

    [Fact]
    public void TerminateTracked_StopsTheStartedProcessFirst_ReportsARefusal_AndStillStopsTheRest()
    {
        var table = new ScriptedTable(
        [
            new ProcessEntry(100, 4, "msedge.exe"),
            new ProcessEntry(101, 100, "msedge.exe"),
            new ProcessEntry(102, 100, "msedge.exe"),
            new ProcessEntry(200, 4, "msedge.exe")
        ]);
        var order = new List<int>();
        table.Root.OnTerminate = () => order.Add(100);
        var exiting = table.Processes[101] = new FakeTracked(101, RootCreated + 1)
        {
            // What Windows answers for a process that is already exiting.
            OnTerminate = () => throw new System.ComponentModel.Win32Exception(5)
        };
        var other = table.Processes[102] = new FakeTracked(102, RootCreated + 2) { OnTerminate = () => order.Add(102) };
        var unrelated = table.Processes[200] = new FakeTracked(200, RootCreated + 3);
        BrowserProcessTree? tree = null;
        tree = new BrowserProcessTree(table.Root, () => tree!.TerminateTracked(), table.Snapshot, table.Open);
        tree.Refresh();

        var error = Assert.Throws<AggregateException>(tree.KillTree);

        Assert.IsType<System.ComponentModel.Win32Exception>(Assert.Single(error.InnerExceptions));
        Assert.Equal([100, 102], order);
        Assert.False(exiting.HasExited);
        Assert.True(other.HasExited);
        Assert.Equal(0, unrelated.Terminations);
    }

    [Fact]
    public void Dispose_ReleasesEveryTrackedHandle()
    {
        var table = new ScriptedTable([new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(101, 100, "msedge.exe")]);
        var child = table.Processes[101] = new FakeTracked(101, RootCreated + 1);
        var tree = table.Tree();
        tree.Refresh();

        tree.Dispose();

        Assert.True(child.Disposed);
        Assert.True(table.Root.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_ReleasesEveryUnpromotedHandle_WhenTheVerificationSnapshotFails(bool cancelled)
    {
        // Child 101 is tracked. Its child 102 and the browser's new child 103 are opened, and the snapshot that would
        // confirm them fails (WAS-106).
        var table = new ScriptedTable([new ProcessEntry(100, 4, "msedge.exe"), new ProcessEntry(101, 100, "msedge.exe")]);
        var tracked = table.Processes[101] = new FakeTracked(101, RootCreated + 1);
        Exception failure = cancelled ? new OperationCanceledException() : new System.ComponentModel.Win32Exception(8);
        // A snapshot taken once more handles than this have been opened fails.
        var failAfterOpened = int.MaxValue;
        var tree = new BrowserProcessTree(
            table.Root, () => table.Root.Exited = true,
            () => table.Opened.Count > failAfterOpened ? throw failure : table.Snapshot(), table.Open);
        tree.Refresh();
        failAfterOpened = table.Opened.Count;
        table.Script(
        [
            new ProcessEntry(100, 4, "msedge.exe"),
            new ProcessEntry(101, 100, "msedge.exe"),
            new ProcessEntry(102, 101, "msedge.exe"),
            new ProcessEntry(103, 100, "msedge.exe")
        ]);
        var grandchild = table.Processes[102] = new FakeTracked(102, RootCreated + 2);
        var child = table.Processes[103] = new FakeTracked(103, RootCreated + 3);

        Assert.Same(failure, Assert.Throws(failure.GetType(), () => tree.Refresh()));

        Assert.Equal([102, 103], table.Opened.Skip(failAfterOpened));
        Assert.Equal(1, grandchild.Disposals);
        Assert.Equal(1, child.Disposals);
        Assert.Equal(0, tracked.Disposals);
        Assert.Equal(0, table.Root.Disposals);

        tree.Dispose();

        Assert.Equal(1, tracked.Disposals);
        Assert.Equal(1, table.Root.Disposals);
        Assert.Equal(1, grandchild.Disposals);
        Assert.Equal(1, child.Disposals);
    }

    /// <summary>
    /// Snapshots returned in order (the last one repeats) and the process each id opens as at that moment. The started
    /// process is id 100.
    /// </summary>
    private sealed class ScriptedTable
    {
        private readonly Queue<ProcessEntry[]> _snapshots = new();
        private ProcessEntry[] _last;

        public ScriptedTable(params ProcessEntry[][] snapshots)
        {
            foreach (var snapshot in snapshots)
                _snapshots.Enqueue(snapshot);
            _last = snapshots[^1];
        }

        public FakeTracked Root { get; } = new(100, RootCreated);

        public Dictionary<int, FakeTracked> Processes { get; } = [];

        public List<int> Opened { get; } = [];

        public void Script(params ProcessEntry[][] snapshots)
        {
            foreach (var snapshot in snapshots)
                _snapshots.Enqueue(snapshot);
            _last = snapshots[^1];
        }

        public BrowserProcessTree Tree() => new(Root, () => Root.Exited = true, Snapshot, Open);

        public IReadOnlyList<ProcessEntry> Snapshot() => _snapshots.TryDequeue(out var next) ? next : _last;

        public BrowserProcessTree.TrackedProcess? Open(int id, string name)
        {
            Opened.Add(id);
            return Processes.GetValueOrDefault(id);
        }
    }

    private sealed class FakeTracked(int id, long createdAt) : BrowserProcessTree.TrackedProcess
    {
        public override int Id => id;

        public override string Name => "msedge.exe";

        public override long? CreatedAt => createdAt;

        public override bool HasExited => Exited;

        public override bool HasStopped => Exited;

        public bool Exited { get; set; }

        public int Terminations { get; private set; }

        public bool Disposed => Disposals > 0;

        public int Disposals { get; private set; }

        /// <summary>Runs before the process ends; an exception leaves it running.</summary>
        public Action? OnTerminate { get; set; }

        public override void Terminate()
        {
            Terminations++;
            OnTerminate?.Invoke();
            Exited = true;
        }

        public override void Dispose() => Disposals++;
    }
}
