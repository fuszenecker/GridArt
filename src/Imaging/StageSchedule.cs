using System.Diagnostics;

namespace gridart.Imaging;

/// <summary>
/// Decides when the next intermediate mosaic ("stage") is due during a long run.
/// </summary>
/// <remarks>
/// <para>
/// Two things this has to get right. First, only one stage may be produced at a time: the check
/// happens on whichever worker thread of the parallel tile loader gets there first, so claiming a slot
/// and releasing it must be atomic, or several threads would render previews simultaneously and fight
/// over the same output file.
/// </para>
/// <para>
/// Second, a stage must never dominate the run it is reporting on. Rendering and PNG-encoding a large
/// mosaic can take seconds, so the next slot is pushed out to at least
/// <see cref="BackoffFactor"/> × the time the last stage cost. A stage that takes 20s on a huge grid
/// therefore settles to one every ~80s instead of hogging a 60s interval.
/// </para>
/// </remarks>
internal sealed class StageSchedule
{
    /// <summary>A stage may consume at most 1/<see cref="BackoffFactor"/> of the run's wall clock.</summary>
    private const int BackoffFactor = 4;

    private readonly Lock gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan interval;

    private TimeSpan nextDue;
    private bool busy;
    private int claimed;

    /// <param name="interval">Gap between stages; <see cref="TimeSpan.Zero"/> or less disables them.</param>
    public StageSchedule(TimeSpan interval)
    {
        this.interval = interval;
        Enabled = interval > TimeSpan.Zero;
        // The first stage is due one whole interval in, so a run that finishes quickly writes none.
        nextDue = interval;
    }

    public bool Enabled { get; }

    /// <summary>Number of stages written so far.</summary>
    public int Written { get; private set; }

    /// <summary>
    /// Claims the next stage slot if one is due, handing back its 1-based number. The caller must call
    /// <see cref="Release"/> when finished, whether or not a stage was actually produced.
    /// </summary>
    public bool TryClaim(out int index)
    {
        index = 0;

        if (!Enabled)
        {
            return false;
        }

        lock (gate)
        {
            if (busy || clock.Elapsed < nextDue)
            {
                return false;
            }

            busy = true;
            index = ++claimed;
            return true;
        }
    }

    /// <summary>Releases the slot and schedules the next stage, backing off by what this one cost.</summary>
    /// <param name="cost">Wall-clock time the claim occupied.</param>
    /// <param name="produced">
    /// Whether a stage file was actually written. A claim that produced nothing does not consume a
    /// number, so stage files stay consecutively numbered.
    /// </param>
    public void Release(TimeSpan cost, bool produced)
    {
        lock (gate)
        {
            busy = false;

            if (produced)
            {
                Written++;
            }
            else
            {
                claimed--;
            }

            var wait = interval > cost * BackoffFactor ? interval : cost * BackoffFactor;
            nextDue = clock.Elapsed + wait;
        }
    }
}
