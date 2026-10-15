namespace ETS2LA.Game.Telemetry;

public sealed record TelemetryActivity(bool Active, bool Paused, bool ProducerPresent, long ReadAt, long AdvancedAt, long ResumedAt)
{
    public static TelemetryActivity Empty { get; } = new(false, false, false, 0, 0, 0);
    public bool IsLive(long now) => Active && ProducerPresent && now - ReadAt < 400 && now - AdvancedAt < 400;
    public bool IsSuspended(long now) => Active && ProducerPresent && now - ReadAt < 400 &&
        (Paused || AdvancedAt > 0 && now - AdvancedAt >= 400);
    public bool AcceptsControls(long now, params long[] controls) => IsLive(now) && !Paused &&
        controls.All(at => at >= ResumedAt && now - at < 400);
}

/// <summary>Distinguishes a frozen producer from a controller failure. Window focus is never consulted.</summary>
public sealed class TelemetryFrameClock
{
    private ulong sdkTime, simulatedTime, renderTime;
    private bool observed;
    public TelemetryActivity Activity { get; private set; } = TelemetryActivity.Empty;

    public TelemetryActivity Observe(long now, bool active, bool paused, bool producerPresent, ulong sdk, ulong simulation, ulong render)
    {
        bool advanced = !observed || sdk != sdkTime || simulation != simulatedTime || render != renderTime;
        bool resumed = active && producerPresent && advanced && (!Activity.Active || !Activity.ProducerPresent ||
            Activity.Paused || Activity.AdvancedAt > 0 && now - Activity.AdvancedAt >= 400);
        long frame = active && producerPresent && advanced ? now : Activity.AdvancedAt;
        Activity = new(active, paused, producerPresent, now, frame, resumed ? now : Activity.ResumedAt);
        sdkTime = sdk; simulatedTime = simulation; renderTime = render; observed = true;
        return Activity;
    }
}
