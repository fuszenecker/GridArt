using System.Diagnostics;

namespace gridart.Progress;

/// <summary>
/// Reports progress through <see cref="ILogger"/>, so it flows through the normal logging pipeline:
/// log-level filters apply, and any provider configured on the host (console, file, Seq, OpenTelemetry)
/// receives it without this class knowing about them.
/// </summary>
/// <remarks>
/// <para>
/// Interim updates are logged at <see cref="LogLevel.Information"/> as discrete, self-contained lines
/// — never carriage-return redraws, which would corrupt a structured or file-based log. Each update
/// names its values (<c>Phase</c>, <c>Current</c>, <c>Total</c>, <c>Percent</c>, <c>Elapsed</c>), so a
/// structured sink can query them rather than parse the rendered text.
/// </para>
/// <para>
/// Because every update is a real log record, they are emitted on a time interval rather than per
/// unit: a 17,000-cell phase produces a handful of lines, not 17,000.
/// </para>
/// </remarks>
public sealed class LoggingProgressReporter : IProgressReporter
{
    /// <summary>
    /// Minimum gap between interim updates. Long enough that a big job stays readable, short enough
    /// that the run never looks hung.
    /// </summary>
    internal static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger logger;
    private readonly TimeSpan updateInterval;

    public LoggingProgressReporter(ILogger<LoggingProgressReporter> logger)
        : this(logger, DefaultUpdateInterval)
    {
    }

    /// <summary>
    /// Test seam: lets a test shorten the throttle instead of sleeping through the real interval.
    /// </summary>
    internal LoggingProgressReporter(ILogger logger, TimeSpan updateInterval)
    {
        this.logger = logger;
        this.updateInterval = updateInterval;
    }

    public IProgressPhase Begin(string title, long total = 0, string unit = "items")
    {
        if (total > 0)
        {
            logger.LogInformation("{Phase}: starting, {Total:N0} {Unit}.", title, total, unit);
        }
        else
        {
            logger.LogInformation("{Phase}: starting.", title);
        }

        return new Phase(logger, title, total, unit, updateInterval);
    }

    private sealed class Phase : IProgressPhase
    {
        private readonly ILogger logger;
        private readonly string title;
        private readonly string unit;
        private readonly long updateIntervalTicks;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();

        private long total;
        private long current;
        private long lastReportTicks;
        private bool finished;

        public Phase(ILogger logger, string title, long total, string unit, TimeSpan updateInterval)
        {
            this.logger = logger;
            this.title = title;
            this.total = total;
            this.unit = unit;
            updateIntervalTicks = (long)(updateInterval.TotalSeconds * Stopwatch.Frequency);
        }

        public TimeSpan Elapsed => stopwatch.Elapsed;

        public void Advance(long delta = 1)
        {
            // Advance is called from parallel tile loading, so the counter must be atomic.
            var value = Interlocked.Add(ref current, delta);

            if (!logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            // Raw timer ticks, compared against an interval converted to the same unit — cheaper than
            // materialising a TimeSpan on every unit of a 17,000-cell phase.
            var elapsedTicks = stopwatch.ElapsedTicks;
            var last = Interlocked.Read(ref lastReportTicks);

            if (elapsedTicks - last < updateIntervalTicks)
            {
                return;
            }

            // Whoever wins the exchange logs, so concurrent callers cannot emit duplicate updates for
            // the same interval.
            if (Interlocked.CompareExchange(ref lastReportTicks, elapsedTicks, last) != last)
            {
                return;
            }

            Report(value);
        }

        public void SetTotal(long value) => Interlocked.Exchange(ref total, value);

        public void Dispose()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            stopwatch.Stop();

            var done = Interlocked.Read(ref current);

            // A phase that never counted anything is a single indivisible step (decoding one image);
            // reporting "0 items" would be noise, so only the duration is useful.
            if (done == 0)
            {
                logger.LogInformation(
                    "{Phase}: done in {Elapsed}.",
                    title, Format(stopwatch.Elapsed));
                return;
            }

            var perSecond = stopwatch.Elapsed.TotalSeconds >= 0.1
                ? done / stopwatch.Elapsed.TotalSeconds
                : 0d;

            if (perSecond > 0)
            {
                // The rate deliberately repeats no placeholder name: duplicate names in one message
                // template collide in a structured sink.
                logger.LogInformation(
                    "{Phase}: done — {Current:N0} {Unit} in {Elapsed} ({Rate:N0}/s).",
                    title, done, unit, Format(stopwatch.Elapsed), perSecond);
            }
            else
            {
                logger.LogInformation(
                    "{Phase}: done — {Current:N0} {Unit} in {Elapsed}.",
                    title, done, unit, Format(stopwatch.Elapsed));
            }
        }

        private void Report(long value)
        {
            var snapshot = Interlocked.Read(ref total);

            if (snapshot <= 0)
            {
                logger.LogInformation(
                    "{Phase}: {Current:N0} {Unit} so far ({Elapsed}).",
                    title, value, unit, Format(stopwatch.Elapsed));
                return;
            }

            var fraction = Math.Clamp(value / (double)snapshot, 0d, 1d);

            // Only estimate a remaining time once the sample is big enough to mean anything.
            // Elapsed.Ticks, not ElapsedTicks: the latter counts raw timer ticks, not 100ns units.
            var eta = fraction > 0.02
                ? Format(TimeSpan.FromTicks((long)(stopwatch.Elapsed.Ticks / fraction * (1 - fraction))))
                : "?";

            logger.LogInformation(
                "{Phase}: {Percent:F0}% — {Current:N0}/{Total:N0} {Unit}, {Elapsed} elapsed, ~{Eta} left.",
                title, fraction * 100, value, snapshot, unit, Format(stopwatch.Elapsed), eta);
        }

        private static string Format(TimeSpan span) => span.TotalSeconds < 1
            ? $"{span.TotalMilliseconds:F0}ms"
            : span.TotalSeconds < 60
                ? $"{span.TotalSeconds:F1}s"
                : $"{(int)span.TotalMinutes}m{span.Seconds:D2}s";
    }
}
