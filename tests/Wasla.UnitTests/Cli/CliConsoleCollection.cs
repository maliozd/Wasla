namespace Wasla.UnitTests.Cli;

/// <summary>
/// Test classes that use the process-wide <see cref="Console"/>: they redirect <see cref="Console.Out"/> or
/// <see cref="Console.Error"/> to capture CLI output, or run CLI code that writes to the console. Not run in parallel
/// with any other test (WAS-171).
/// </summary>
/// <remarks>
/// A capture saves the current writer, installs its own and restores the saved one afterwards. Run in parallel, one
/// capture can save another's writer and restore it after that writer was disposed, so a later console write anywhere
/// fails with <see cref="ObjectDisposedException"/>, and a capture can also collect another test's output. Any new test
/// class that redirects the console or runs a CLI command that writes to it joins this collection.
/// </remarks>
[CollectionDefinition(nameof(CliConsoleCollection), DisableParallelization = true)]
public sealed class CliConsoleCollection;
