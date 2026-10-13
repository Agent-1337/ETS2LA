using System.Numerics;
using ETS2LA.Game.Perception;

namespace ETS2LA.State.Autonomy;

public sealed record DrivingScene(long Now, bool Enabled, bool Ready, Vector3 Ego, float Speed, float RigLength,
    RouteCorridor Route, IReadOnlyList<SceneObstacle> Obstacles, bool TrafficFresh, bool VisionFresh,
    bool LeftIndicator, bool RightIndicator, bool PaidToll, int? ClosedGateId, float GateDistance, bool TollPrompt, float SpeedLimit = 20, bool MergeCoverage = true);
public sealed record ManeuverDecision(string Status, float Brake = 0, float? SpeedCap = null, int Signal = 0, bool PayToll = false);

/// <summary>Conservative behavior planning on known corridors; never crosses into opposing traffic.</summary>
public sealed class ManeuverPlanner
{
    private string? hazardId;
    private long blockedSince;
    private int originLane, targetLane, changeDirection;
    private Vector3 bypassedPosition;
    private long changeStarted, settledAt;
    private bool passing;
    private int? tollGate;
    private int tollAttempts;
    private long tollStationarySince, lastPayment;
    private bool tollConfirmed;
    private string? handover;

    public void Reset()
    {
        hazardId = null; blockedSince = 0; originLane = targetLane = changeDirection = 0;
        passing = false; tollGate = null; tollAttempts = 0; tollStationarySince = lastPayment = 0;
        tollConfirmed = false;
        handover = null;
    }

    public ManeuverDecision Decide(DrivingScene scene)
    {
        if (!scene.Enabled) { Reset(); return new("Manual driving"); }
        if (!scene.Ready) return new("Waiting for live game data", 0.65f);
        if (handover != null) return new(handover, 0.7f);
        if (!float.IsFinite(scene.Speed + scene.SpeedLimit + scene.RigLength) || scene.RigLength is < 1 or > 120) return new("Invalid vehicle telemetry; braking", 0.7f);
        if (!scene.Route.IsValid) return new("Waiting for a valid route", 0.65f);
        if (scene.Speed < -0.1f) return new("Automatic maneuvers suspended in reverse");
        if (hazardId != null && !scene.VisionFresh) return new("Known obstruction: visual confirmation lost; braking", 0.7f);
        if (scene.ClosedGateId is int gate && scene.GateDistance is > 0 and < 45)
            return Toll(scene, gate);
        tollGate = null; tollAttempts = 0; tollStationarySince = lastPayment = 0; tollConfirmed = false;

        if (targetLane != 0)
        {
            if (!scene.TrafficFresh || !scene.VisionFresh || !scene.MergeCoverage) return new("Braking: maneuver sensors unavailable", 0.75f);
            float egoAlongNow = scene.Route.Project(scene.Ego).Along;
            float stopDistance = scene.Speed * scene.Speed / 8 + scene.Speed * 1.5f + 8;
            if (scene.Obstacles.Any(obstacle => {
                var projected = scene.Route.Project(obstacle.Position);
                float distance = projected.Along - egoAlongNow - obstacle.Length / 2 - 4;
                return distance > -2 && distance < stopDistance && Math.Abs(projected.Lateral) < obstacle.Radius + 1.35f;
            })) return new("Braking: obstacle on the lane-change trajectory", 0.8f);
            float nativeChangeDistance = 5 * Math.Max(20, scene.SpeedLimit);
            if (scene.Now - changeStarted > (nativeChangeDistance / 5.55f + 8) * 1000)
            { handover = "Lane change not confirmed; manual takeover required"; return new(handover, 0.7f); }
            if (scene.Route.Lane == targetLane && !scene.Route.LaneChangeInProgress)
            {
                targetLane = 0; settledAt = scene.Now;
                if (passing) return new("Passing the obstruction", SpeedCap: 8.33f);
                originLane = 0;
                return new("Returned to the driving lane", SpeedCap: 11.11f);
            }
            return new(changeDirection < 0 ? "Changing lane left" : "Changing lane right", SpeedCap: 8.33f);
        }

        float egoAlong = scene.Route.Project(scene.Ego).Along;
        var hazards = scene.Obstacles.Select(o => (Obstacle: o, P: scene.Route.Project(o.Position)))
            .Where(x => x.P.Along - egoAlong > -3 && x.P.Along - egoAlong < 250 && Math.Abs(x.Obstacle.Position.Y - scene.Ego.Y) < 3 &&
                        Math.Abs(x.P.Lateral) < x.Obstacle.Radius + 1.35f && x.Obstacle.Speed < 1.5f)
            .OrderBy(x => x.P.Along).ToArray();
        var hazard = hazards.FirstOrDefault();
        if (hazard.Obstacle == null)
        {
            hazardId = null;
            if (passing && scene.Now - settledAt > 10000 && scene.TrafficFresh && scene.VisionFresh && scene.MergeCoverage)
            {
                float clearance = Vector3.Dot(scene.Ego - bypassedPosition, scene.Route.Forward(scene.Ego));
                int returnDirection = -changeDirection;
                int returnLane = returnDirection < 0 ? scene.Route.LeftLane : scene.Route.RightLane;
                float returnOffset = returnDirection < 0 ? scene.Route.LeftOffset : scene.Route.RightOffset;
                if (clearance > scene.RigLength + 20 && originLane != 0 && returnLane == originLane && GapClear(scene, returnOffset))
                {
                    passing = false; targetLane = originLane; changeDirection = returnDirection; changeStarted = scene.Now;
                    return new("Returning to the original lane after the trailer has cleared", SpeedCap: 8.33f, Signal: returnDirection);
                }
            }
            return new(passing ? "Passing the obstruction" : scene.VisionFresh && scene.TrafficFresh ? "Monitoring traffic and roadworks" : "Sensor confirmation unavailable; slowing", SpeedCap: passing ? 8.33f : scene.VisionFresh && scene.TrafficFresh ? null : 5.55f);
        }
        var obstacle = hazard.Obstacle;
        if (hazardId != obstacle.Id) { hazardId = obstacle.Id; blockedSince = scene.Now; }
        float gap = hazard.P.Along - egoAlong - obstacle.Length / 2 - 4;
        bool roadwork = obstacle.Id.StartsWith("vision:", StringComparison.Ordinal);
        float stopping = scene.Speed * scene.Speed / 8 + scene.Speed * 1.5f + 10;
        float brake = gap <= stopping ? Math.Clamp((stopping - gap) / Math.Max(10, stopping) + 0.2f, 0.2f, 1) : 0;
        if (!scene.TrafficFresh || !scene.VisionFresh || !scene.MergeCoverage) return new("Obstacle ahead; waiting for reliable sensors", Math.Max(brake, gap < 25 ? 0.6f : 0), 5.55f);
        if (scene.LeftIndicator || scene.RightIndicator || scene.Route.LaneChangeInProgress) return new("Obstacle ahead; driver lane change takes priority", brake, 5.55f);
        if (scene.Now - blockedSince < (roadwork ? 500 : 5000)) return new(roadwork ? "Confirming a roadwork obstruction" : "Following stopped traffic", brake, 5.55f);
        // Slow first, so a complete lane transition fits before the obstruction.
        float requiredDistance = 5 * Math.Max(20, scene.SpeedLimit) + scene.RigLength + 12;
        if (scene.Speed > 8.6f) return new("Slowing before the obstruction", Math.Max(brake, 0.45f), 5.55f);
        if (gap < requiredDistance) return new("Stopped: insufficient distance for a complete lane change", Math.Max(brake, gap < 30 ? 0.7f : 0), 0);
        int direction = scene.Route.LeftLane != 0 && GapClear(scene, scene.Route.LeftOffset) ? -1 :
                        roadwork && scene.Route.RightLane != 0 && GapClear(scene, scene.Route.RightOffset) ? 1 : 0;
        if (direction == 0) return new("Waiting: adjacent lane occupied or unavailable", Math.Max(brake, gap < 25 ? 0.6f : 0), 5.55f);
        if (passing) return new("Another obstruction ahead; slowing", Math.Max(brake, 0.3f), 5.55f);
        originLane = scene.Route.Lane;
        targetLane = direction < 0 ? scene.Route.LeftLane : scene.Route.RightLane;
        changeDirection = direction; changeStarted = scene.Now; bypassedPosition = obstacle.Position; passing = true;
        return new(direction < 0 ? "Indicating left to bypass the obstruction" : "Indicating right to bypass the obstruction", SpeedCap: 8.33f, Signal: direction);
    }

    private static bool GapClear(DrivingScene scene, float offset)
    {
        float egoAlong = scene.Route.Project(scene.Ego).Along;
        foreach (var obstacle in scene.Obstacles)
        {
            var p = scene.Route.Project(obstacle.Position);
            if (Math.Abs(obstacle.Position.Y - scene.Ego.Y) > 3 || Math.Abs(p.Lateral - offset) > obstacle.Radius + 1.6f) continue;
            float gap = p.Along - egoAlong;
            float ahead = Math.Max(30, scene.Speed * 4) + obstacle.Length / 2;
            float transitionSeconds = 5 * Math.Max(20, scene.SpeedLimit) / 5.55f + 3;
            float behind = Math.Max(scene.RigLength + 15, (obstacle.Speed + scene.Speed) * transitionSeconds) + obstacle.Length / 2;
            if (gap > -behind && gap < ahead) return false;
        }
        return true;
    }

    private ManeuverDecision Toll(DrivingScene scene, int gate)
    {
        if (tollGate != gate) { tollGate = gate; tollAttempts = 0; tollStationarySince = lastPayment = 0; tollConfirmed = false; }
        if (scene.PaidToll) tollConfirmed = true;
        if (tollConfirmed) return new("Toll paid; waiting for the barrier", 0.6f);
        if (scene.Speed > 0.25f || scene.GateDistance > 12)
        {
            tollStationarySince = 0;
            return new("Approaching a closed barrier", scene.GateDistance < 9 ? 0.7f : 0, Math.Min(5.55f, MathF.Sqrt(Math.Max(0, (scene.GateDistance - 7) * 4))));
        }
        if (tollStationarySince == 0) tollStationarySince = scene.Now;
        if (!scene.TrafficFresh || !scene.VisionFresh || !scene.TollPrompt) return new("Stopped at barrier; waiting for a toll payment prompt", 0.6f);
        if (scene.Now - tollStationarySince >= 800 && tollAttempts < 2 && (lastPayment == 0 || scene.Now - lastPayment > 4000))
        {
            tollAttempts++; lastPayment = scene.Now;
            return new("Paying the toll", 0.6f, PayToll: true);
        }
        return new(tollAttempts >= 2 && scene.Now - lastPayment > 4000 ? "Toll not confirmed; manual action required" : "Waiting for toll payment confirmation", 0.6f);
    }
}
