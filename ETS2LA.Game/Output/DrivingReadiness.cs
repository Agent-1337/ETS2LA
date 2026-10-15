namespace ETS2LA.Game.Output;

public enum DrivingPhase { Manual, Preparing, Active, Recovering, Handover, Suspended }

/// <summary>Acquire controls before engagement. Missing inputs are not emergency brake commands.</summary>
public sealed class DrivingReadiness
{
    private long requestedAt, missingAt;
    private bool engaged;
    public DrivingPhase Phase { get; private set; }
    public bool HasEngaged => engaged;
    public DrivingPhase Update(long now, bool requested, bool ready, bool suspended = false)
    {
        if (!requested) { engaged = false; requestedAt = missingAt = 0; return Phase = DrivingPhase.Manual; }
        if (Phase == DrivingPhase.Handover) return Phase;
        if (suspended) { requestedAt = now; missingAt = 0; return Phase = DrivingPhase.Suspended; }
        if (requestedAt == 0) requestedAt = now;
        if (ready) { engaged = true; missingAt = 0; return Phase = DrivingPhase.Active; }
        if (!engaged) return Phase = now - requestedAt < 8000 ? DrivingPhase.Preparing : DrivingPhase.Handover;
        if (missingAt == 0) missingAt = now;
        return Phase = now - missingAt < 1200 ? DrivingPhase.Recovering : DrivingPhase.Handover;
    }
}
