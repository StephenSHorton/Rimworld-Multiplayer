namespace Multiplayer.Common.Desyncs;

/// <summary>
/// The timer ticks a soft pawns or things difference turned the steering ledger on for (the referee's verdict
/// carries them). It switches at the same timer tick on every game the verdict reached before the window's start.
/// <see cref="Reset"/> at every load and at the end of a session: the window counts in the old game's timer, and a
/// window left open when a session ended stayed on in the next one, whose timer starts near 0.
/// </summary>
public class LedgerWindow
{
    public enum ArmResult { Empty, TooLate, Extended, Armed }

    public int From { get; private set; } = -1;
    public int Until { get; private set; } = -1;
    public bool On { get; private set; }

    public bool Armed => From >= 0;

    public ArmResult Arm(int from, int until, int timer)
    {
        if (until <= from) return ArmResult.Empty;
        if (timer >= from) return ArmResult.TooLate;
        if (Armed)
        {
            // Already armed or on: the window grows to the later end, from the start already set
            if (until > Until) Until = until;
            return ArmResult.Extended;
        }

        From = from;
        Until = until;
        return ArmResult.Armed;
    }

    /// <summary>Every timer tick: true when it switched on or off at this tick.</summary>
    public bool Tick(int timer)
    {
        if (!Armed) return false;
        if (!On && timer >= From && timer < Until)
        {
            On = true;
            return true;
        }
        if (timer >= Until)
        {
            Reset();
            return true;
        }
        return false;
    }

    /// <summary>Forgets the window; true when one was armed or on.</summary>
    public bool Reset()
    {
        var was = Armed;
        On = false;
        From = Until = -1;
        return was;
    }
}
