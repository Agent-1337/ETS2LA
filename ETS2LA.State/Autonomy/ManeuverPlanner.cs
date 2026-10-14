using System.Numerics;
using ETS2LA.Game.Perception;

namespace ETS2LA.State.Autonomy;

public sealed record DrivingScene(long Now, bool Enabled, bool Ready, Vector3 Ego, float Speed, float RigLength,
    RouteCorridor Route, IReadOnlyList<SceneObstacle> Obstacles, bool TrafficFresh, bool VisionFresh,
    bool LeftIndicator, bool RightIndicator, bool PaidToll, int? ClosedGateId, float GateDistance, bool TollPrompt, float SpeedLimit = 20, bool MergeCoverage = true, RigGeometry? Rig = null);
public sealed record ManeuverDecision(string Status, float Brake = 0, float? SpeedCap = null, int Signal = 0, bool PayToll = false, float? StoppingDistance = null);

/// <summary>Conservative behavior planning on known corridors; never crosses into opposing traffic.</summary>
public sealed class ManeuverPlanner
{
    private string? hazardId;
    private long blockedSince;
    private int originLane, targetLane, changeDirection;
    private readonly Dictionary<string, SceneObstacle> bypassed = [];
    private SceneObstacle? rememberedHazard;
    private SceneObstacle[] rememberedMarkers = [];
    private float longestRig;
    private float changeSpeed = 13.89f;
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
        bypassed.Clear(); rememberedHazard = null; rememberedMarkers = []; longestRig = 0;
    }

    public ManeuverDecision Decide(DrivingScene scene)
    {
        if (!scene.Enabled) { Reset(); return new("Manual driving"); }
        if (!scene.Ready) return new("Waiting for live game data", 0.65f);
        if (handover != null) return new(handover, 0.7f);
        if (!float.IsFinite(scene.Speed + scene.SpeedLimit + scene.RigLength) || scene.RigLength is < 1 or > 120) return new("Invalid vehicle telemetry; braking", 0.7f);
        if (!scene.Route.IsValid) return new("Waiting for a valid route", 0.65f);
        if (scene.Speed < -0.1f) return new("Automatic maneuvers suspended in reverse");
        if (scene.ClosedGateId is int gate && scene.GateDistance is > 0 and < 45)
            return Toll(scene, gate);
        tollGate = null; tollAttempts = 0; tollStationarySince = lastPayment = 0; tollConfirmed = false;
        if (passing) { longestRig = Math.Max(longestRig, scene.RigLength); UpdateBypassZone(scene); }

        if (targetLane != 0)
        {
            // A focus change cannot cancel a lane transition already requested.
            // Native traffic/route control remains live; only new visual maneuvers
            // require a fresh image. Losing actual traffic data still blocks motion.
            if (!scene.TrafficFresh) return new("Braking: live maneuver traffic unavailable", 0.75f);
            float egoAlongNow = scene.Route.Project(scene.Ego).Along;
            float stopDistance = scene.Speed * scene.Speed / 8 + scene.Speed * 1.5f + 8;
            if (scene.Obstacles.Any(obstacle => {
                var projected = scene.Route.Project(obstacle.Position);
                float distance = projected.Along - egoAlongNow - obstacle.Length / 2 - 4;
                return distance > -2 && distance < stopDistance && ObstructionEvidence.SameRoadSurface(obstacle, scene) &&
                    (ObstructionEvidence.IsVisual(obstacle) ? ObstructionEvidence.BlocksLane(obstacle, scene) :
                        Math.Abs(projected.Lateral) < obstacle.Radius + (scene.Rig?.HalfWidth ?? 1.3f));
            })) return new("Braking: obstacle on the lane-change trajectory", 0.8f);
            float nativeChangeDistance = NativeChangeDistance(scene);
            if (scene.Now - changeStarted > (nativeChangeDistance / 5.55f + 15) * 1000)
            { handover = "Lane change not confirmed; manual takeover required"; return new(handover, 0.7f); }
            if (scene.Route.Lane == targetLane && !scene.Route.LaneChangeInProgress)
            {
                targetLane = 0; settledAt = scene.Now;
                if (passing) return new("Passing the obstruction; monitoring the trailer", SpeedCap: 13.89f);
                originLane = 0;
                bypassed.Clear(); rememberedHazard = null;
                return new("Returned to the driving lane", SpeedCap: RigTurnGuard.SpeedCap(scene));
            }
            return new(changeDirection < 0 ? "Changing lane left" : "Changing lane right", SpeedCap: changeSpeed);
        }

        float egoAlong = scene.Route.Project(scene.Ego).Along;
        // A confirmed stationary obstruction does not disappear because one fresh
        // image or the closest-vehicle SDK list temporarily omits it. An explicit
        // observation that it moved/cleared can release it; disabling also resets it.
        IEnumerable<SceneObstacle> candidates = scene.Obstacles;
        if (rememberedHazard is { } previous && scene.Obstacles.Any(o => o.Id == previous.Id && (o.Speed >= 1.5f ||
            !ObstructionEvidence.SameRoadSurface(o, scene) || Math.Abs(scene.Route.Project(o.Position).Lateral) >= o.Radius + (scene.Rig?.HalfWidth ?? 1.3f))))
        { rememberedHazard = null; rememberedMarkers = []; }
        if (!passing && rememberedHazard is { } knownHazard && !scene.Obstacles.Any(o => o.Id == knownHazard.Id))
            candidates = candidates.Append(knownHazard);
        if (!passing && rememberedHazard != null)
            candidates = candidates.Concat(rememberedMarkers.Where(marker => !scene.Obstacles.Any(o => o.Id == marker.Id) && marker.Id != rememberedHazard.Id));
        var evaluatedScene = scene with { Obstacles = candidates.ToArray() };
        var hazards = evaluatedScene.Obstacles.Select(o => (Obstacle: o, P: scene.Route.Project(o.Position)))
            .Where(x => x.P.Along - egoAlong > -3 && x.P.Along - egoAlong < 250 && ObstructionEvidence.SameRoadSurface(x.Obstacle, scene) &&
                        ObstructionEvidence.BlocksLane(x.Obstacle, evaluatedScene))
            .OrderBy(x => x.P.Along).ToArray();
        var hazard = hazards.FirstOrDefault();
        if (hazard.Obstacle == null)
        {
            hazardId = null;
            if (!passing) { rememberedHazard = null; rememberedMarkers = []; }
            if (passing && scene.Now - settledAt > 10000 && scene.TrafficFresh && scene.MergeCoverage)
            {
                int returnDirection = -changeDirection;
                int returnLane = returnDirection < 0 ? scene.Route.LeftLane : scene.Route.RightLane;
                float returnOffset = returnDirection < 0 ? scene.Route.LeftOffset : scene.Route.RightOffset;
                if (TailClearedZone(scene) && originLane != 0 && returnLane == originLane && GapClear(scene, returnOffset))
                {
                    passing = false; targetLane = originLane; changeDirection = returnDirection; changeStarted = scene.Now; changeSpeed = 13.89f;
                    return new("Returning after the entire trailer and work zone have cleared", SpeedCap: 13.89f, Signal: returnDirection);
                }
            }
            bool clear = passing && TailClearedZone(scene);
            return new(passing ? clear ? "Work zone cleared; waiting for a return gap" : "Passing the work zone; waiting for trailer clearance" :
                "Monitoring traffic and roadworks", SpeedCap: passing && !clear ? 13.89f : RigTurnGuard.SpeedCap(scene));
        }
        var obstacle = hazard.Obstacle;
        rememberedHazard = obstacle;
        rememberedMarkers = ObstructionEvidence.IsMarker(obstacle) ? evaluatedScene.Obstacles.Where(o => ObstructionEvidence.IsMarker(o) &&
            Math.Abs(scene.Route.Project(o.Position).Along - hazard.P.Along) < 18 && ObstructionEvidence.BlocksLane(o, evaluatedScene)).ToArray() : [];
        if (hazardId != obstacle.Id) { hazardId = obstacle.Id; blockedSince = scene.Now; }
        float gap = hazard.P.Along - egoAlong - obstacle.Length / 2 - 4;
        bool roadwork = ObstructionEvidence.IsVisual(obstacle) && obstacle.Kind is not ("car" or "truck");
        bool nativeObstacle = !ObstructionEvidence.IsVisual(obstacle);
        bool nativeParked = nativeObstacle && obstacle.Id.StartsWith("parked:", StringComparison.Ordinal);
        if (!scene.TrafficFresh || !nativeObstacle && !scene.VisionFresh || !scene.MergeCoverage) return Approach(scene, gap, "Confirmed obstacle ahead; automatic passing suspended");
        if (scene.LeftIndicator || scene.RightIndicator || scene.Route.LaneChangeInProgress) return Approach(scene, gap, "Obstacle ahead; driver lane change takes priority");
        if (scene.Rig is { TrailerCount: > 0, HasLiveTail: false }) return Approach(scene, gap, "Waiting for complete trailer geometry before passing");
        int confirmation = nativeParked ? 350 :
            roadwork ? obstacle.Confirmed ? 0 : 500 : 1800;
        if (scene.Now - blockedSince < confirmation) return Approach(scene, gap, roadwork ? "Confirming a blocked lane" : "Confirming stopped traffic");
        // Native transitions have a fixed spatial curve. Check where the rig
        // actually clears each obstruction, rather than adding the entire rig
        // length to a requirement to finish the whole curve before its near face.
        float nativeDistance = NativeChangeDistance(scene);
        if (nativeDistance < 35) return Approach(scene, gap, "Waiting for a suitable lane-change section");
        float width = Math.Max(3, Math.Max(Math.Abs(scene.Route.LeftOffset), Math.Abs(scene.Route.RightOffset)));
        float mergeSpeed = Math.Min(scene.SpeedLimit > 0 ? scene.SpeedLimit + 5 / 3.6f : 8.33f, MathF.Sqrt(nativeDistance * nativeDistance / (6 * width)));
        if (scene.Speed > mergeSpeed + 0.5f) return Approach(scene, gap, "Slowing to a stable lane-change speed", mergeSpeed);
        // Include a temporarily missing remembered obstruction in clearance too.
        bool leftFits = scene.Route.LeftLane != 0 && LaneChangeClearance.CanClear(evaluatedScene, scene.Route.LeftOffset);
        bool rightFits = (roadwork || nativeParked) && scene.Route.RightLane != 0 && LaneChangeClearance.CanClear(evaluatedScene, scene.Route.RightOffset);
        if (!leftFits && !rightFits)
            return scene.Speed < 0.3f ? new("Stopped before obstruction; no collision-free native lane-change path", 0.85f, 0) :
                Approach(scene, gap, "Preparing to stop; no collision-free native lane-change path");
        int direction = leftFits && GapClear(scene, scene.Route.LeftOffset) ? -1 : rightFits && GapClear(scene, scene.Route.RightOffset) ? 1 : 0;
        if (direction == 0) return Approach(scene, gap, "Confirmed obstruction; adjacent lane occupied or unavailable");
        if (passing) return Approach(scene, gap, "Another confirmed obstruction ahead");
        originLane = scene.Route.Lane;
        targetLane = direction < 0 ? scene.Route.LeftLane : scene.Route.RightLane;
        changeDirection = direction; changeStarted = scene.Now; changeSpeed = mergeSpeed; passing = true; longestRig = scene.RigLength;
        bypassed.Clear(); bypassed[obstacle.Id] = obstacle; UpdateBypassZone(scene);
        return new(direction < 0 ? "Indicating left to bypass the obstruction" : "Indicating right to bypass the obstruction", SpeedCap: mergeSpeed, Signal: direction);
    }

    private static ManeuverDecision Approach(DrivingScene scene, float gap, string status, float ceiling = float.PositiveInfinity)
    {
        float cap = Math.Min(ceiling, MathF.Sqrt(Math.Max(0, (gap - 10) * 3)));
        float available = Math.Max(0.5f, gap - 8 - Math.Max(0, scene.Speed) * 0.4f);
        float deceleration = scene.Speed * scene.Speed / (2 * available);
        float brake = gap < 12 ? 0.85f : scene.Speed > cap + 0.25f ? Math.Clamp(0.2f + deceleration / 6, 0.35f, 1) : 0;
        // Far-away warnings do not instantly force the truck down to 20 km/h.
        return new(status, brake, cap, StoppingDistance: Math.Max(0, gap - 8));
    }

    private void UpdateBypassZone(DrivingScene scene)
    {
        if (bypassed.Count == 0 || scene.Route.LaneChangeInProgress) return;
        float offset = scene.Route.Lane == originLane ? 0 : scene.Route.LeftLane == originLane ? scene.Route.LeftOffset :
            scene.Route.RightLane == originLane ? scene.Route.RightOffset : float.NaN;
        if (!float.IsFinite(offset)) return;
        // Expand through a connected work zone, not just the first barricade.
        // Keep these world positions beyond the vision tracker's short occlusion timeout.
        var candidates = scene.Obstacles.Where(o => o.Speed < 1.5f && ObstructionEvidence.SameRoadSurface(o, scene) &&
            Math.Abs(scene.Route.Project(o.Position).Lateral - offset) < o.Radius + (scene.Rig?.HalfWidth ?? 1.3f) - 0.35f).ToArray();
        for (int pass = 0; pass < 4; pass++)
        foreach (var obstacle in candidates)
        {
            float along = scene.Route.Project(obstacle.Position).Along;
            if (bypassed.Values.Any(known => Math.Abs(scene.Route.Project(known.Position).Along - along) < 60 + (known.Length + obstacle.Length) / 2))
                bypassed[obstacle.Id] = obstacle;
        }
    }

    private bool TailClearedZone(DrivingScene scene)
    {
        if (bypassed.Count == 0) return false;
        float end = bypassed.Values.Max(o => scene.Route.Project(o.Position).Along + o.Length / 2);
        float rear = scene.Route.Project(scene.Ego).Along - Math.Max(longestRig, scene.RigLength);
        if (scene.Rig is { HasLiveTail: true, Footprint.Count: > 0 } rig)
            rear = Math.Min(rear, rig.Footprint.Min(p => scene.Route.Project(p).Along));
        // Distance follows the curved road. A tractor's tangent projection can
        // claim clearance while the articulated trailer is still beside the works.
        return rear > end + Math.Max(20, scene.Speed * 2);
    }

    private static float NativeChangeDistance(DrivingScene scene) => LaneChangeClearance.NativeDistance(scene);

    private static bool GapClear(DrivingScene scene, float offset)
    {
        float egoAlong = scene.Route.Project(scene.Ego).Along;
        foreach (var obstacle in scene.Obstacles)
        {
            var p = scene.Route.Project(obstacle.Position);
            if (!ObstructionEvidence.SameRoadSurface(obstacle, scene) || Math.Abs(p.Lateral - offset) > obstacle.Radius + 1.6f) continue;
            float gap = p.Along - egoAlong;
            float transitionSeconds = NativeChangeDistance(scene) / Math.Max(5.55f, Math.Min(scene.Speed, 13.89f)) + 3;
            float ahead = Math.Max(30, scene.Speed * 4) + Math.Max(0, scene.Speed - obstacle.Speed) * transitionSeconds + obstacle.Length / 2;
            float behind = Math.Max(scene.RigLength + 15, Math.Max(0, obstacle.Speed - scene.Speed) * transitionSeconds) + obstacle.Length / 2;
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
            return new("Approaching a closed barrier", scene.GateDistance < 9 ? 0.7f : 0, Math.Min(5.55f, MathF.Sqrt(Math.Max(0, (scene.GateDistance - 7) * 4))), StoppingDistance: Math.Max(0,scene.GateDistance-7));
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
