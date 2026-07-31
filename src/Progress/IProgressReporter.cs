namespace gridart.Progress;

/// <summary>
/// Reports long-running work. Kept behind an interface so runs can be silenced (<c>--quiet</c>) and
/// so tests are not coupled to a particular logging setup.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Begins a phase. Dispose the returned phase to finish it.
    /// </summary>
    /// <param name="title">Short human label, e.g. "Loading tiles".</param>
    /// <param name="total">
    /// Expected number of units, or 0 when unknown — an unknown total reports activity without a
    /// percentage rather than inventing one.
    /// </param>
    /// <param name="unit">Plural noun for the units, used in the log lines.</param>
    IProgressPhase Begin(string title, long total = 0, string unit = "items");
}

/// <summary>A single unit of work in progress.</summary>
public interface IProgressPhase : IDisposable
{
    /// <summary>Records completed units. Safe to call from multiple threads.</summary>
    void Advance(long delta = 1);

    /// <summary>Sets the expected total once it becomes known.</summary>
    void SetTotal(long total);

    /// <summary>Elapsed time so far.</summary>
    TimeSpan Elapsed { get; }
}

/// <summary>Reports nothing. Used by tests and by <c>--quiet</c>.</summary>
public sealed class NullProgressReporter : IProgressReporter
{
    public static NullProgressReporter Instance { get; } = new();

    public IProgressPhase Begin(string title, long total = 0, string unit = "items") => new NullPhase();

    private sealed class NullPhase : IProgressPhase
    {
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public TimeSpan Elapsed => stopwatch.Elapsed;

        public void Advance(long delta = 1) { }

        public void SetTotal(long total) { }

        public void Dispose() => stopwatch.Stop();
    }
}
