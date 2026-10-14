namespace ETS2LA.State.Autonomy;

/// <summary>One neural job at a time, bounded GPU duty cycle, with a fast independent geometry worker.</summary>
public static class PerceptionBudget
{
    public const int FastPeriodMilliseconds = 200;
    public static int NeuralInterval(double inferenceMilliseconds) => double.IsFinite(inferenceMilliseconds) ? (int)Math.Clamp(inferenceMilliseconds * 10, 1500, 60000) : 60000;
    public static bool NeedsNeuralConfirmation(long now, long lastRun, double lastDuration, bool jobRunning) =>
        !jobRunning && now - lastRun >= NeuralInterval(lastDuration);
}
