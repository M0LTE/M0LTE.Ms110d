namespace M0LTE.Ms110d;

/// <summary>
/// Which mini-probes of a burst are cyclically shifted (D.5.2.2), for a receiver that must
/// accept two transmitter conventions without being told which one it is hearing.
/// </summary>
/// <remarks>
/// Data frames are numbered from 0 across the whole burst; <c>g</c> is the frame the probe
/// FOLLOWS (-1 is the probe that ends the preamble, which is never shifted).
/// <list type="bullet">
/// <item><b>Current</b> (what this library has always sent): shifted when
/// <c>(g + 2) % F == 0</c>, F the selected interleaver's frame count.</item>
/// <item><b>Standard</b> (D.5.2.2): shifted only when <c>g % L == L - 2</c>, L the LONG
/// interleaver's frame count for the waveform (256, 128 or 64), whatever interleaver is
/// selected.</item>
/// </list>
/// F divides L for every waveform, so the standard pattern is a subset of the current one:
/// the two disagree only at probes the current rule shifts and the standard rule does not,
/// and never at all for the Long interleaver (F == L). At a disagreeing probe the receiver
/// scores both hypotheses against the received probe (<see cref="AddEvidence"/>), and
/// follows the accumulated evidence. The current rule applies until the standard pattern is
/// CLEARLY PROVEN (the summed score reaches +<see cref="SwitchEvidence"/>, about one decisive probe), never on a provisional sign,
/// so a burst sent with the current pattern is decoded exactly as before however noisy or
/// faded its probes are; at -<see cref="LockEvidence"/> the current rule is confirmed and
/// scoring stops.
/// </remarks>
internal sealed class ProbePattern
{
    // Each score is in [-1, 1] (1 = the standard hypothesis fits the probe perfectly and the
    // current one not at all), so 3 means a few probes agreeing clearly.
    private const double LockEvidence = 3.0;

    // The standard pattern is followed once the summed score is clearly positive: one
    // decisive probe (scores of real probes were 0.54 to 0.99 for the right hypothesis), never
    // a provisional sign. Faded probes score 0 and so never move the sum.
    internal const double SwitchEvidence = 0.5;

    private int _frames = 1;
    private int _longFrames = 1;
    private double _evidence;
    private bool _locked;

    /// <summary>Sets the selected and long interleaver frame counts and forgets any evidence.</summary>
    public void Configure(int frames, int longFrames)
    {
        _frames = frames;
        _longFrames = longFrames;
        Reset();
    }

    /// <summary>Forgets the evidence (start of a burst).</summary>
    public void Reset()
    {
        _evidence = 0;
        _locked = false;
        Scored = 0;
        PeakEvidence = 0;
    }

    /// <summary>How many probes have been scored this burst (diagnostic).</summary>
    internal int Scored { get; private set; }

    /// <summary>Highest running score this burst (diagnostic, for tests).</summary>
    internal double PeakEvidence { get; private set; }

    /// <summary>The evidence is conclusive and no more probes are scored.</summary>
    public bool Locked => _locked;

    /// <summary>Current rule: the selected interleaver's boundary.</summary>
    public static bool CurrentRule(long g, int frames) => g >= 0 && (g + 2) % frames == 0;

    /// <summary>Standard rule: the long interleaver's boundary.</summary>
    public static bool StandardRule(long g, int longFrames) => g >= 0 && g % longFrames == longFrames - 2;

    /// <summary>The current rule for the probe after frame g, at this burst's interleaver.</summary>
    public bool Current(long g) => CurrentRule(g, _frames);

    /// <summary>The standard rule for the probe after frame g, at this burst's waveform.</summary>
    public bool Standard(long g) => StandardRule(g, _longFrames);

    /// <summary>Whether this probe's two hypotheses still need scoring.</summary>
    public bool NeedsEvidence(long g) => !_locked && Current(g) != Standard(g);

    /// <summary>The probe after frame g is shifted, under the pattern the evidence favours.</summary>
    public bool Boundary(long g)
    {
        bool current = Current(g);
        bool standard = Standard(g);
        return current == standard ? current : (_evidence >= SwitchEvidence ? standard : current);
    }

    /// <summary>Adds one probe's score: positive when the standard hypothesis fits better.</summary>
    public void AddEvidence(double score)
    {
        Scored++;
        _evidence += score;
        PeakEvidence = Math.Max(PeakEvidence, _evidence);
        if (Math.Abs(_evidence) >= LockEvidence)
        {
            _locked = true;
        }
    }
}
