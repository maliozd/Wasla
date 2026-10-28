namespace Wasla.UnitTests.Realtime;

/// <summary>
/// <see cref="RealtimeLoadTests"/>: the live hub under load, with its server, its clients and the notifier's send timeout
/// all running on this process's shared thread pool. Not run in parallel with any other test.
/// </summary>
/// <remarks>
/// The tests assert time bounds, such as a send to a client that stopped reading ending within the notifier's 2-second
/// bound. Another test class busy at the same time holds thread-pool threads, so the timer that ends the send fires late
/// and the measured send includes that wait: on a loaded CI runner one send took 3.9 s although the notifier had
/// cancelled it at 2 s. Run alone, the bounds measure the hub and the notifier, not the other tests.
/// </remarks>
[CollectionDefinition(nameof(RealtimeLoadCollection), DisableParallelization = true)]
public sealed class RealtimeLoadCollection;
