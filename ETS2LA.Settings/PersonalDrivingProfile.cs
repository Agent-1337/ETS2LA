using System.Text.Json.Nodes;

namespace ETS2LA.Settings;

/// <summary>The fixed, effective settings for this personal build, also applied to plugin settings.</summary>
public static class PersonalDrivingProfile
{
    public const float SpeedOffsetMetresPerSecond = 5f / 3.6f;
    public static bool IsEnabled => File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "DistributionSource.txt"))
        && File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "DistributionSource.txt")).Trim() == "Local";

    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowSettings.json"] = """{"StayOnTop":false}""",
        ["StateSettings.json"] = """
            {"DisplayUnits":0,"DesiredSpeedStaticOffset":1.3888889,"DesiredSpeedPercentageOffset":0,"FallbackSpeed":8.333334}
            """,
        ["AssistanceSettings.json"] = """
            {"SeparateCruiseAndSteering":false,"PauseWhenUnstable":true,"AccelerationResponse":2,"SteeringSensitivity":1,"FollowingDistance":2,"SetSpeedBehaviourOption":0,"SpeedLimitWarningOption":0,"CollisionAvoidanceOption":3,"MaximumSpeed":0,"EnabledTrafficRules":[0,1,2]}
            """,
        ["tumppi066.adaptivecruisecontrol.json"] = """
            {"AccelerationDampeningFactor":0.8,"PositivePedalMultiplier":1,"IgnoredConstraints":[],"OverrideFollowingDistance":true,"FollowingDistanceOverride":3,"CenterlineTolerance":3,"MaxLateralAcceleration":1.25,"MinimumSpeed":4.166667,"MaximumDistance":200,"PointStep":2,"StoppingDistance":8}
            """
    };

    public static string ApplyOverrides(string fileName, string json)
    {
        if (!Overrides.TryGetValue(fileName, out string? overrides)) return json;
        var settings = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var fixedValues = JsonNode.Parse(overrides)!.AsObject();
        foreach (var entry in fixedValues) settings[entry.Key] = entry.Value?.DeepClone();
        return settings.ToJsonString();
    }

    public static bool HasOverrides(string fileName) => Overrides.ContainsKey(fileName);

    public static string ApplyIfEnabled(string fileName, string json) => IsEnabled ? ApplyOverrides(fileName, json) : json;
}
