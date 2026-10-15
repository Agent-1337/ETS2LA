using System.Diagnostics;
using System.Numerics;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using TruckLib;
using ETS2LA.Game;
using ETS2LA.Game.Data;

namespace ETS2LA.State.Autonomy;

public sealed record AutonomyStatus(string Action, string Perception, IReadOnlyList<SceneObstacle> Obstacles, RouteCorridor Route, Vector3 Ego)
{
    public static AutonomyStatus Empty { get; } = new("Manual driving", "Native road monitoring starting", [], RouteCorridor.Empty, Vector3.Zero);
}

/// <summary>Runs independently of UI focus and existing plugins, using their live data and control channels.</summary>
public sealed class AutonomySupervisor : IDisposable
{
    public static AutonomySupervisor Current { get; } = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly ManeuverPlanner planner = new();
    private readonly NativeCruiseController cruise = new();
    private readonly NativeRouteRecovery routeRecovery = new();
    private readonly ControlChannelDefinition cruiseChannel = new() { Id = "ETS2LA.Autonomy.Cruise", Timeout = 0.35f };
    private volatile bool tollNeedsPrompt;
    private readonly ObstacleTracker tracker = new();
    private readonly RouteHistory routeHistory = new();
    private readonly ControlChannelDefinition controls = new() { Id = "ETS2LA.Autonomy.Actions", Timeout = 0.25f };
    private volatile VisionSnapshot vision = VisionSnapshot.Empty;
    private volatile object? path;
    private volatile TrafficData? traffic;
    private volatile ParkedVehicleData? parked;
    private volatile SemaphoreData? semaphores;
    private long pathAt, trafficAt, parkedAt, gatesAt, cameraAt, observedAt;
    private volatile CameraData? camera;
    private volatile AutonomyStatus status = AutonomyStatus.Empty;
    private bool wasPaid;
    private int freshFrames;
    private long paidAt;
    private long lastUiAt;
    private Task? decisionLoop, visionLoop;
    private volatile bool started;
    public AutonomyStatus Status => status;
    public event EventHandler? Changed;
    private void OnPath(object data)
    {
        path = data; Interlocked.Exchange(ref pathAt, Environment.TickCount64);
        // This callback runs on the planner's publishing thread. Re-seeding here
        // does not race its list iteration and takes effect on its next tick.
        if (ApplicationState.Current.EnableAssists && data.GetType().GetField("HasError")?.GetValue(data) is true &&
            ETS2LA.Backend.PluginBackend.Current.PluginHandler?.LoadedPlugins.FirstOrDefault(p => p.Info.Id == "tumppi066.pathfinding") is { } plugin)
        {
            var telemetry = GameTelemetry.Current.GetCurrentData();
            if (telemetry.sdkActive && !telemetry.paused && routeRecovery.TryRecover(plugin,
                telemetry.truckPlacement.coordinate.ToVector3(), telemetry.truckPlacement.rotation.ToVector3(), Environment.TickCount64))
                Logger.Info("Navigation recovered at a road boundary using native map geometry.");
        }
    }
    private void OnTraffic(TrafficData data) { traffic = NativeSceneSnapshot.Copy(data); Interlocked.Exchange(ref trafficAt, Environment.TickCount64); }
    private void OnParked(ParkedVehicleData data) { parked = NativeSceneSnapshot.Copy(data); Interlocked.Exchange(ref parkedAt, Environment.TickCount64); }
    private void OnGates(SemaphoreData data) { semaphores = NativeSceneSnapshot.Copy(data); Interlocked.Exchange(ref gatesAt, Environment.TickCount64); }
    private void OnCamera(CameraData data)
    {
        camera = new CameraData { projection = data.projection, rotation = data.rotation, position = data.position, cx = data.cx, cy = data.cy, truckPosition = data.truckPosition, truckRotation = data.truckRotation, fov = data.fov };
        Interlocked.Exchange(ref cameraAt, Environment.TickCount64);
    }

    public void Start()
    {
        if (started) return;
        started = true;
        // Attachment, axle and trailer placement data were opt-in. Clearance must
        // never silently operate on a tractor-only snapshot.
        GameTelemetry.Current.ReadTrailerData = true;
        Events.Current.Subscribe<object>("Pathfinding.PlannedPathData", OnPath);
        Events.Current.Subscribe(TrafficProvider.Current.EventString, (Action<TrafficData>)OnTraffic);
        Events.Current.Subscribe(ParkedVehiclesProvider.Current.EventString, (Action<ParkedVehicleData>)OnParked);
        Events.Current.Subscribe(SemaphoreProvider.Current.EventString, (Action<SemaphoreData>)OnGates);
        Events.Current.Subscribe(CameraProvider.Current.EventString, (Action<CameraData>)OnCamera);
        decisionLoop = Task.Run(DecisionLoop);
        #if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) visionLoop = Task.Run(VisionLoop);
        else vision = VisionSnapshot.Empty with { Status = "Direct game capture requires Windows 10 version 1903 or later" };
        #else
        vision = VisionSnapshot.Empty with { Status = "Visual roadwork detection currently requires Windows" };
        #endif
    }

    private async Task DecisionLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { Update(); }
            catch (Exception ex)
            {
                Logger.Error($"Autonomy supervisor: {ex.Message}");
                if (ApplicationState.Current.EnableAssists) Events.Current.Publish(DrivingEnvelope.Event, new DrivingEnvelope(Environment.TickCount64 + 500, 0, null, "Autonomy update unavailable", HasValidRoute: false));
            }
            try { await Task.Delay(33, cancellation.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private void Update()
    {
        long now = Environment.TickCount64;
        var telemetry = GameTelemetry.Current.GetCurrentData();
        var state = ApplicationState.Current;
        bool ready = telemetry.sdkActive && !telemetry.paused && now - Interlocked.Read(ref pathAt) < 1000;
        var ego = telemetry.truckPlacement.coordinate.ToVector3();
        var route = ready ? RouteSnapshotBuilder.Build(path, ego) : RouteCorridor.Empty;
        if (!state.EnableAssists || !telemetry.sdkActive) routeHistory.Clear();
        else route = routeHistory.Include(route, ego);
        var observation = vision;
        if (state.EnableAssists && observation.CapturedAt != observedAt)
        {
            observedAt = observation.CapturedAt;
            tracker.Update(observation.Obstacles, observation.CapturedAt);
            freshFrames = observation.HasForwardView ? freshFrames + 1 : 0;
        }
        if (!observation.HasForwardView) freshFrames = 0;
        if (!state.EnableAssists) { tracker.Clear(); freshFrames = 0; }
        // Screen detections are diagnostic observations. Native positions own
        // automatic braking/merging; a mislocalized cone cannot stop the truck.
        var visualObstacles = tracker.Confirmed(now);
        var obstacles = new List<SceneObstacle>();
        bool freshTraffic = now - Interlocked.Read(ref trafficAt) < 500 && now - Interlocked.Read(ref parkedAt) < 500;
        freshTraffic &= (traffic?.vehicles.All(v => v.id == 0 || VehicleGeometry.IsValid(v) && float.IsFinite(v.speed)) ?? false) &&
                        (parked?.vehicles.All(v => v.id == 0 || VehicleGeometry.IsValid(v)) ?? false);
        // A full closest-40 frame can hide additional vehicles. It cannot certify
        // that the whole merge corridor is clear.
        bool trafficCoverage = (traffic?.vehicles.Count(v => v.id != 0 && v.Size.X > 0.2f) ?? 40) < 40;
        if (freshTraffic)
        {
            foreach (var vehicle in traffic?.vehicles ?? [])
            {
                if (vehicle.id == 0 || vehicle.Size.X < 0.2f || !float.IsFinite(vehicle.Position.X + vehicle.Position.Y + vehicle.Position.Z)) continue;
                obstacles.Add(VehicleGeometry.Observe(vehicle, $"traffic:{vehicle.id}", "traffic vehicle", Math.Abs(vehicle.speed), route));
                for (int i = 0; i < Math.Min(vehicle.trailer_count, vehicle.trailers.Length); i++)
                {
                    var trailer = vehicle.trailers[i];
                    if (trailer.Size.X > 0.2f) obstacles.Add(VehicleGeometry.Observe(trailer, $"traffic:{vehicle.id}:trailer:{i}", "traffic trailer", Math.Abs(vehicle.speed), route));
                }
            }
            foreach (var vehicle in parked?.vehicles ?? [])
                if (vehicle.id != 0 && vehicle.Size.X > 0.2f && float.IsFinite(vehicle.Position.X + vehicle.Position.Y + vehicle.Position.Z))
                    obstacles.Add(VehicleGeometry.Observe(vehicle, $"parked:{vehicle.id}", "parked vehicle", 0, route));
        }
        int? gateId = null; float gateDistance = float.PositiveInfinity;
        if (route.IsValid && now - Interlocked.Read(ref gatesAt) < 500)
        {
            float along = route.Project(ego).Along;
            foreach (var gate in semaphores?.semaphores ?? [])
            {
                if (gate.type != SemaphoreType.GATE || gate.state is not ((int)GateStates.CLOSED) and not ((int)GateStates.CLOSING)) continue;
                var p = route.Project(gate.GetWorldCoordinates());
                float distance = p.Along - along;
                if (distance > 0 && distance < gateDistance && Math.Abs(p.Lateral) < 2.3f)
                { gateId = gate.id; gateDistance = distance; }
            }
        }
        bool paid = telemetry.specialBool.tollgate;
        if (paid && !wasPaid) paidAt = now;
        wasPaid = paid;
        var rig = VehicleGeometry.MeasureRig(telemetry, ego);
        float rigLength = rig.Length;
        bool visualFresh = observation.HasForwardView && now - observation.CapturedAt < 1800 && freshFrames >= 3;
        var scene = new DrivingScene(now, state.EnableAssists, ready, ego, telemetry.truckFloat.speed, rigLength, route, obstacles,
            freshTraffic, visualFresh, telemetry.truckBool.blinkerLeftActive, telemetry.truckBool.blinkerRightActive,
            paidAt > 0 && now - paidAt < 8000, gateId, gateDistance, observation.TollPrompt, telemetry.truckFloat.speedLimit, trafficCoverage, rig);
        var decision = planner.Decide(scene);
        var turn = decision.Signal == 0 ? WideTurnPlanner.Build(scene) : new WideTurnPlan(route,null,0);
        SteeringTrajectory.Publish(turn.Frame);
        float? signalStop = now - Interlocked.Read(ref gatesAt) < 500 ? RouteSnapshotBuilder.SignalStopDistance(path, scene, semaphores) : null;
        var demand = cruise.Calculate(scene with {Route=turn.Route}, state.DesiredSpeed, decision, signalStop);
        tollNeedsPrompt = scene.ClosedGateId != null && scene.GateDistance < 12 && Math.Abs(scene.Speed) < 0.3f;
        if (state.EnableAssists && ready && route.IsValid)
            Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent {
                ChannelDefinition = cruiseChannel, Variables = new() { aforward = demand.Throttle, abackward = demand.Brake }
            });
        Events.Current.Publish(DrivingEnvelope.Event, new DrivingEnvelope(now + 600, decision.Brake, decision.SpeedCap, decision.Status, state.EnableAssists, decision.StoppingDistance, ready && route.IsValid));
        if (ready && state.EnableAssists && (decision.Signal != 0 || decision.PayToll))
        {
            int requestedLane = decision.Signal < 0 ? route.LeftLane : route.RightLane;
            bool nativeLaneRequested = decision.Signal != 0 && RouteSnapshotBuilder.RequestLaneChange(path,ego,route.Lane,requestedLane);
            Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
            {
                ChannelDefinition = controls,
                Properties = new() { BooleanType = ControlBooleanType.TrueToToggle },
                Variables = new() { lblinker = !nativeLaneRequested && decision.Signal < 0 ? true : null,
                    rblinker = !nativeLaneRequested && decision.Signal > 0 ? true : null, activate = decision.PayToll ? true : null }
            });
        }
        status = new(turn.IsActive && decision.Status == "Monitoring traffic and roadworks" ? "Taking a wider arc for trailer clearance" : decision.Status,
            GameSettings.Current.EnableVisualRoadworkDiagnostics || tollNeedsPrompt ? observation.Status :
            "Monitoring native traffic and parked vehicles", obstacles.Concat(visualObstacles).ToArray(), turn.Route, ego);
        if (now - lastUiAt >= 250)
        {
            lastUiAt = now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    #if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362")]
    private async Task VisionLoop()
    {
        GroundingDinoDetector? detector = null;
        TollPromptReader? promptReader = null;
        Task<(long At, SceneObstacle[] Obstacles, double Duration)>? neural = null;
        SceneObstacle[] neuralObstacles = [];
        long lastNeural = -10000, idleSince = 0;
        double lastDuration = 150;
        string provider = "ML on demand";
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Perception");
            using var capture = new GameFrameCapture();
            using var patterns = new PixelWorkspace();
            using var cones = new PixelWorkspace();
            while (!cancellation.IsCancellationRequested)
            {
                long cycleStarted = Environment.TickCount64;
                if (neural?.IsCompleted == true)
                {
                    try
                    {
                        var result = neural.GetAwaiter().GetResult();
                        neuralObstacles = result.Obstacles;
                        lastDuration = result.Duration;
                        provider = detector?.ExecutionProvider ?? "ML unavailable";
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Optional visual confirmation: {ex.Message}"); provider = "ML unavailable";
                        lastDuration = 6000; lastNeural = cycleStarted;
                    }
                    neural = null;
                }
                var telemetry = GameTelemetry.Current.GetCurrentData();
                var runningGame = ApplicationState.Current.RunningGame;
                var pose = camera;
                bool diagnostics = GameSettings.Current.EnableVisualRoadworkDiagnostics;
                bool active = ApplicationState.Current.EnableAssists && telemetry.sdkActive && !telemetry.paused && runningGame != null && (diagnostics || tollNeedsPrompt);
                if (!active)
                {
                    capture.Dispose();
                    if (idleSince == 0) idleSince = cycleStarted;
                    if (cycleStarted - idleSince > 30000 && neural == null)
                    {
                        detector?.Dispose(); detector = null;
                        promptReader?.Dispose(); promptReader = null;
                        capture.Dispose();
                        neuralObstacles = [];
                    }
                    vision = vision with { HasForwardView = false, TollPrompt = false, Status = "Visual perception waiting for live game data" };
                }
                else if (pose == null || cycleStarted - Interlocked.Read(ref cameraAt) > 500)
                    vision = vision with { HasForwardView = false, TollPrompt = false, Status = "Waiting for a fresh game camera" };
                else
                {
                    idleSince = 0;
                    using var captured = capture.Capture(runningGame!.ExecutablePath);
                    if (captured == null)
                        vision = vision with { HasForwardView = false, TollPrompt = false, Status = capture.Status };
                    else
                    {
                        var frame = captured.Image;
                        long capturedAt = captured.CapturedAt;
                        var ego = telemetry.truckPlacement.coordinate.ToVector3();
                        var route = status.Route;
                        if (!route.IsValid || Vector3.Distance(pose.truckPosition, ego) > 5)
                            vision = vision with { HasForwardView = false, TollPrompt = false, Status = "Waiting for a synchronized route and camera" };
                        else
                        {
                            var forward = route.Forward(ego);
                            var center = CameraGeometry.GroundPoint(CameraGeometry.ViewProjection(pose), new(0.5f, 0.7f), ego.Y);
                            var centerWorld = center + new Vector3(pose.cx * 512, 0, pose.cy * 512);
                            if (centerWorld == null || Vector3.Dot(Vector3.Normalize(centerWorld.Value - ego), forward) < 0.8f)
                                vision = vision with { HasForwardView = false, TollPrompt = false, Status = "Visual perception requires a forward-facing camera" };
                            else if (!diagnostics)
                            {
                                bool prompt = (promptReader ??= new TollPromptReader(assets)).Read(frame);
                                vision = new(capturedAt, [], [], true, "Reading the toll payment prompt", prompt);
                            }
                            else
                            {
                                var fastStarted = Stopwatch.StartNew();
                                var masks = status.Obstacles.Where(o => !ObstructionEvidence.IsVisual(o))
                                    .Select(o => CameraGeometry.VehicleMask(o, pose, frame.Width, frame.Height, forward)).Where(o => o != null).Cast<ObjectDetection>().ToArray();
                                var detections = RoadworkEvidenceDetector.Detect(frame, masks, patterns)
                                    .Concat(ConeEvidenceDetector.Detect(frame, cones)).ToArray();
                                var located = detections.Select(d => CameraGeometry.Locate(d, pose, frame.Width, frame.Height, ego, forward, route.Points))
                                    .Where(o => o != null).Select(o => o! with { ObservedAt = capturedAt }).ToList();
                                located.AddRange(neuralObstacles.Where(o => cycleStarted - o.ObservedAt <= 1800 &&
                                    !located.Any(fast => fast.Kind == o.Kind && Vector3.Distance(fast.Position, o.Position) < 1.25f)).ToArray());
                                bool prompt = false;
                                if (Math.Abs(telemetry.truckFloat.speed) < 0.3f && status.Action.Contains("barrier", StringComparison.OrdinalIgnoreCase))
                                    prompt = (promptReader ??= new TollPromptReader(assets)).Read(frame);
                                vision = new(capturedAt, detections, located, Environment.TickCount64 - capturedAt < 600,
                                    $"Roadwork monitoring: {located.Count} candidates, fast {fastStarted.ElapsedMilliseconds} ms ({provider})", prompt);
                                if (PerceptionBudget.NeedsNeuralConfirmation(cycleStarted, lastNeural, lastDuration, neural != null))
                                {
                                    // Latest-only handoff: one owned ROI, one job, no frame queue.
                                    var region = new System.Drawing.Rectangle((int)(frame.Width * 0.2f), (int)(frame.Height * 0.25f), (int)(frame.Width * 0.64f), (int)(frame.Height * 0.43f));
                                    var owned = frame.Clone(region, frame.PixelFormat);
                                    int width = frame.Width, height = frame.Height;
                                    lastNeural = cycleStarted;
                                    neural = Task.Run(() =>
                                    {
                                        using (owned)
                                        {
                                            if (detector == null)
                                            {
                                                detector = new GroundingDinoDetector(assets);
                                                Logger.Info($"Optional visual confirmation initialized on {detector.DeviceName} ({detector.ExecutionProvider}).");
                                            }
                                            var clock = Stopwatch.StartNew();
                                            var semantic = detector.Detect(owned).Select(d => d with { Box = new(d.Box.Left + region.X, d.Box.Top + region.Y, d.Box.Right + region.X, d.Box.Bottom + region.Y) }).ToArray();
                                            var physical = ObstructionEvidence.PhysicalCandidates(semantic, []);
                                            var objects = physical.Select(d => CameraGeometry.Locate(d, pose, width, height, ego, forward, route.Points))
                                                .Where(o => o != null).Select(o => o! with { ObservedAt = capturedAt }).ToArray();
                                            return (capturedAt, objects, clock.Elapsed.TotalMilliseconds);
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                int remaining = PerceptionBudget.FastPeriodMilliseconds - (int)(Environment.TickCount64 - cycleStarted);
                await Task.Delay(Math.Max(20, remaining), cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"Visual perception unavailable: {ex}");
            vision = vision with { HasForwardView = false, TollPrompt = false, Status = "Visual diagnostics unavailable; native driving remains active" };
        }
        finally
        {
            // Never dispose a session while an outstanding GPU job still owns it.
            if (neural != null) { try { await neural; } catch { } }
            detector?.Dispose(); promptReader?.Dispose();
        }
    }
    #endif

    public void Dispose()
    {
        if (!started || cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        SteeringTrajectory.Publish(null);
        Events.Current.Unsubscribe<object>("Pathfinding.PlannedPathData", OnPath);
        Events.Current.Unsubscribe(TrafficProvider.Current.EventString, (Action<TrafficData>)OnTraffic);
        Events.Current.Unsubscribe(ParkedVehiclesProvider.Current.EventString, (Action<ParkedVehicleData>)OnParked);
        Events.Current.Unsubscribe(SemaphoreProvider.Current.EventString, (Action<SemaphoreData>)OnGates);
        Events.Current.Unsubscribe(CameraProvider.Current.EventString, (Action<CameraData>)OnCamera);
        Events.Current.Publish(DrivingEnvelope.Event, new DrivingEnvelope(0, 0, null, ""));
    }
}
