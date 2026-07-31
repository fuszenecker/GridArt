using gridart.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace gridart.Tests;

/// <summary>
/// The reporter's contract is the log records it emits, so these tests capture them through a fake
/// <see cref="ILogger"/> and assert on both the rendered text and the named values a structured sink
/// would see.
/// </summary>
public class LoggingProgressReporterTests
{
    [Fact]
    public void Begin_announces_the_phase_and_its_total()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        using (reporter.Begin("Loading tiles", 1200, "tiles"))
        {
        }

        Assert.Contains("Loading tiles", log.Records[0].Message);
        Assert.Contains("1,200", log.Records[0].Message);
        Assert.Contains("tiles", log.Records[0].Message);
    }

    [Fact]
    public void Begin_omits_the_total_when_it_is_unknown()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        using (reporter.Begin("Scanning folder", unit: "files"))
        {
        }

        // "0 files" would be a lie about a phase whose size is not yet known.
        Assert.DoesNotContain("0", log.Records[0].Message);
    }

    [Fact]
    public void Phase_start_and_end_carry_the_phase_name_as_a_named_value()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        using (reporter.Begin("Matching tiles", 10, "cells"))
        {
        }

        Assert.All(log.Records, r => Assert.Equal("Matching tiles", r.Values["Phase"]));
    }

    [Fact]
    public void Everything_is_reported_at_information_level()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        using (var phase = reporter.Begin("Rendering mosaic", 4, "cells"))
        {
            phase.Advance();
            phase.Advance();
        }

        // Progress is normal operating output, not a warning and not diagnostics-only.
        Assert.All(log.Records, r => Assert.Equal(LogLevel.Information, r.Level));
    }

    [Fact]
    public void Dispose_reports_the_completed_count_and_the_unit()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        using (var phase = reporter.Begin("Matching tiles", 3, "cells"))
        {
            phase.Advance(3);
        }

        var last = log.Records[^1];
        Assert.Equal(3L, last.Values["Current"]);
        Assert.Contains("cells", last.Message);
    }

    [Fact]
    public void Dispose_reports_only_a_duration_for_a_phase_that_counted_nothing()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        // Single indivisible steps (decoding one image) never call Advance; "0 items" would be noise.
        using (reporter.Begin("Reading base image"))
        {
        }

        Assert.DoesNotContain("0 items", log.Records[^1].Message);
        Assert.Contains("done in", log.Records[^1].Message);
    }

    [Fact]
    public void Advance_is_throttled_so_a_large_phase_does_not_flood_the_log()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromMinutes(10));

        using (var phase = reporter.Begin("Matching tiles", 20_000, "cells"))
        {
            for (var i = 0; i < 20_000; i++)
            {
                phase.Advance();
            }
        }

        // Start plus completion only: no interim update can fall inside a ten-minute window.
        Assert.Equal(2, log.Records.Count);
    }

    [Fact]
    public void Interim_updates_are_emitted_once_the_interval_has_passed()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        using (var phase = reporter.Begin("Matching tiles", 100, "cells"))
        {
            phase.Advance(25);
            phase.Advance(25);
        }

        Assert.Equal(4, log.Records.Count);
    }

    [Fact]
    public void Interim_updates_report_a_percentage_against_the_total()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        using (var phase = reporter.Begin("Matching tiles", 200, "cells"))
        {
            phase.Advance(50);
        }

        var update = log.Records[1];
        Assert.Equal(25d, Assert.IsType<double>(update.Values["Percent"]));
        Assert.Contains("25%", update.Message);
    }

    [Fact]
    public void Interim_updates_report_a_bare_count_when_the_total_is_unknown()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        using (var phase = reporter.Begin("Scanning folder", unit: "files"))
        {
            phase.Advance(7);
        }

        Assert.DoesNotContain("Percent", log.Records[1].Values.Keys);
        Assert.Equal(7L, log.Records[1].Values["Current"]);
    }

    [Fact]
    public void SetTotal_makes_later_updates_report_a_percentage()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        using (var phase = reporter.Begin("Loading tiles", unit: "tiles"))
        {
            phase.SetTotal(50);
            phase.Advance(10);
        }

        Assert.Equal(50L, log.Records[1].Values["Total"]);
        Assert.Equal(20d, Assert.IsType<double>(log.Records[1].Values["Percent"]));
    }

    [Fact]
    public void Percentage_never_exceeds_one_hundred_when_a_phase_overshoots_its_total()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        // A miscounted total must not produce "250%": the estimate is wrong, the report should not be.
        using (var phase = reporter.Begin("Matching tiles", 4, "cells"))
        {
            phase.Advance(10);
        }

        Assert.Equal(100d, Assert.IsType<double>(log.Records[1].Values["Percent"]));
    }

    [Fact]
    public void Advance_from_many_threads_counts_every_unit()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromMinutes(10));

        // Tile loading advances from inside Parallel.ForEachAsync, so the counter must be atomic.
        using (var phase = reporter.Begin("Loading tiles", 8000, "tiles"))
        {
            Parallel.For(0, 8000, _ => phase.Advance());
        }

        Assert.Equal(8000L, log.Records[^1].Values["Current"]);
    }

    [Fact]
    public void Disposing_twice_reports_the_completion_only_once()
    {
        var log = new CapturingLogger();
        var reporter = new LoggingProgressReporter(log, TimeSpan.FromSeconds(1));

        // TileLibrary disposes its load phase explicitly and again via `using`.
        var phase = reporter.Begin("Loading tiles", 1, "tiles");
        phase.Advance();
        phase.Dispose();
        phase.Dispose();

        Assert.Equal(2, log.Records.Count);
    }

    [Fact]
    public void Elapsed_is_frozen_once_the_phase_is_disposed()
    {
        var reporter = new LoggingProgressReporter(NullLogger.Instance, TimeSpan.FromSeconds(1));

        var phase = reporter.Begin("Matching tiles", 1, "cells");
        phase.Dispose();

        var stopped = phase.Elapsed;
        Thread.Sleep(20);

        Assert.Equal(stopped, phase.Elapsed);
    }

    [Fact]
    public void Nothing_is_reported_when_information_is_filtered_out()
    {
        var log = new CapturingLogger { Enabled = false };
        var reporter = new LoggingProgressReporter(log, TimeSpan.Zero);

        // A log-level filter is the supported way to silence progress, so the interim updates must
        // honour IsEnabled rather than pay for formatting a record nobody will see.
        using (var phase = reporter.Begin("Matching tiles", 100, "cells"))
        {
            phase.Advance(50);
        }

        Assert.DoesNotContain(log.Records, r => r.Message.Contains('%'));
    }

    [Fact]
    public void Null_reporter_writes_nothing_but_still_tracks_time()
    {
        var log = new CapturingLogger();

        using (var phase = NullProgressReporter.Instance.Begin("Matching tiles", 10, "cells"))
        {
            phase.Advance(10);
            phase.SetTotal(20);
            Assert.True(phase.Elapsed >= TimeSpan.Zero);
        }

        Assert.Empty(log.Records);
    }

    private sealed record Record(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    /// <summary>
    /// Captures both the rendered message and the named values, which is what distinguishes an
    /// ILogger-based reporter from one that writes strings to a console.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<Record> records = [];

        public bool Enabled { get; init; } = true;

        public IReadOnlyList<Record> Records
        {
            get { lock (records) { return records.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs.Where(p => p.Key != "{OriginalFormat}"))
                {
                    values[pair.Key] = pair.Value;
                }
            }

            lock (records)
            {
                records.Add(new Record(logLevel, formatter(state, exception), values));
            }
        }
    }
}
