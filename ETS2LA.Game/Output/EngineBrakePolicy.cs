namespace ETS2LA.Game.Output;

public static class EngineBrakePolicy
{
    // motorbrake is the held Jake Brake input, not the engbraketog toggle.
    public static bool ShouldApply(float pedal, float speed, float rpm, bool engineRunning, bool engaged) =>
        engaged && engineRunning && float.IsFinite(pedal + speed + rpm) && pedal < -0.04f && speed > 5 && rpm >= 950;
}
