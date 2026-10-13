using System.Collections;
using System.Diagnostics;

namespace ETS2LA.Game.Output;

/// <summary>Protects against loss of the route required by the driving and traffic constraints.</summary>
public sealed class DrivingPlanGuard
{
    private long lastUpdate;
    private volatile bool valid;
    public bool HasFreshPlan => valid && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastUpdate)).TotalSeconds <= 1;

    public void Update(object? plan)
    {
        if (plan == null) { valid = false; return; }
        var type = plan.GetType();
        object? Read(string name) => type.GetField(name)?.GetValue(plan) ?? type.GetProperty(name)?.GetValue(plan);
        valid = Read("HasError") is false && Read("PlannedPath") is ICollection items && items.Count > 0;
        Interlocked.Exchange(ref lastUpdate, Stopwatch.GetTimestamp());
    }
}
