namespace ETS2LA.Game.Output;

public sealed class EngineBrakePolicy
{
    private long heldSince, releaseSince;
    public bool Applied { get; private set; }

    public bool Update(long now, float pedal, float speed, float rpm, bool engineRunning, bool engaged)
    {
        if (!engaged || !engineRunning || !float.IsFinite(pedal + speed + rpm) || speed < 4 || rpm < 850)
        { Reset(); return false; }
        if (!Applied)
        {
            if (!ShouldApply(pedal, speed, rpm, engineRunning, engaged)) return false;
            Applied = true; heldSince = now; releaseSince = 0;
        }
        // A real acceleration request must persist, and cannot fight a held Jake.
        if (pedal <= -0.015f) releaseSince = 0;
        else if (releaseSince == 0) releaseSince = now;
        if (now - heldSince >= 3000 && releaseSince > 0 && now - releaseSince >= 600) Reset();
        return Applied;
    }

    public void Reset() { Applied = false; heldSince = releaseSince = 0; }
    // motorbrake is a held input, not the engbraketog toggle.
    public static bool ShouldApply(float pedal, float speed, float rpm, bool engineRunning, bool engaged) =>
        engaged && engineRunning && float.IsFinite(pedal + speed + rpm) && pedal <= -0.08f && speed >= 6 && rpm >= 1100;
}
