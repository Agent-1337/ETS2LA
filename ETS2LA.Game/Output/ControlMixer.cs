namespace ETS2LA.Game.Output;

public static class ControlMixer
{
    public static float? Pedals(ControlVariables variables)
    {
        if (variables.aforward == null && variables.abackward == null) return null;
        float brake = Math.Abs(variables.abackward ?? 0);
        if (float.IsFinite(brake) && brake > 0) return -Math.Clamp(brake, 0, 1);
        float throttle = variables.aforward ?? 0;
        return float.IsFinite(throttle) ? Math.Clamp(throttle, 0, 1) : 0;
    }

    public static float Resolve(IEnumerable<Tuple<float, float>> requests, bool brakePriority = false)
    {
        var valid = requests.Where(r => float.IsFinite(r.Item1) && r.Item1 > 0 && float.IsFinite(r.Item2)).ToArray();
        if (valid.Length == 0) return 0;
        if (brakePriority && valid.Any(r => r.Item2 < 0))
            return Math.Clamp(valid.Min(r => r.Item2), -1, 0);
        double weight = valid.Sum(r => (double)r.Item1);
        return (float)Math.Clamp(valid.Sum(r => (double)r.Item1 * r.Item2) / weight, -1, 1);
    }
}
