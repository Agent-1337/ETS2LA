namespace ETS2LA.Game.Output;

/// <summary>A short-lived upper bound on speed/braking; cannot override a stronger brake request.</summary>
public sealed record DrivingEnvelope(long ValidUntil, float Brake, float? MaximumSpeed, string Reason, bool RequireFresh = false, float? StoppingDistance = null)
{
    public const string Event = "ETS2LA.Autonomy.Envelope";
}
