namespace ETS2LA.Game.Output;

/// <summary>Checks the game's response, rather than treating a written pedal as an applied brake.</summary>
public sealed class BrakeResponseGuard
{
    private long startedAt, lastFrame;
    private float initialSpeed;
    public string? Failure { get; private set; }

    public string? Update(long now, long frame, bool enabled, bool active, float command, float speed, float appliedBrake, float appliedThrottle, float? stoppingDistance = null)
    {
        if (!enabled) { Failure = null; Reset(); return null; }
        if (Failure != null) return Failure;
        if (!active || command < 0.35f || Math.Abs(speed) < 0.2f) { Reset(); return null; }
        // Re-reading one telemetry frame is not evidence that the game ignored a command.
        // Stale telemetry is handled independently by the control watchdog.
        if (frame <= lastFrame) return null;
        lastFrame = frame;
        if (startedAt == 0) { startedAt = now; initialSpeed = Math.Abs(speed); return null; }
        if (now - startedAt >= 450 && (!float.IsFinite(appliedBrake + appliedThrottle) ||
            appliedBrake < Math.Min(0.2f, command * 0.4f) || appliedThrottle > 0.15f))
            Failure = "Brake command not applied by the game; emergency pause requested";
        else if (now - startedAt >= 500 && stoppingDistance is float distance && float.IsFinite(distance) &&
            distance < Math.Abs(speed) * 4 && Math.Abs(speed) > 2 &&
            (initialSpeed - Math.Abs(speed)) / ((now - startedAt) / 1000f) < 0.8f * speed * speed / (2 * Math.Max(0.5f, distance)))
            Failure = "Braking is insufficient for the obstruction distance; emergency pause requested";
        else if (now - startedAt >= 1500 && command >= 0.65f && Math.Abs(speed) > 2 && Math.Abs(speed) > initialSpeed - 0.3f)
            Failure = "Truck is not slowing under braking; emergency pause requested";
        // Evaluate deceleration over successive windows, including a brake that
        // initially worked but subsequently stopped slowing the truck.
        else if (now - startedAt >= 1500) { startedAt = now; initialSpeed = Math.Abs(speed); }
        return Failure;
    }

    private void Reset() { startedAt = lastFrame = 0; initialSpeed = 0; }
}
