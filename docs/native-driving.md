# Native driving in the personal build

This build uses game-space traffic, parked vehicles, map paths and semaphores to
drive. Image detections are optional diagnostics and cannot request a brake or a
lane change. Normal driving neither starts a capture session nor loads the neural
model. Alt-Tab is not an input to the driving controller.

## Controls and recovery

- Up enables assistance; Down releases it. The target remains the current speed
  limit plus 5 km/h, subject to traffic, curvature, signals and maneuver limits.
- A deterministic native cruise controller replaces the ACC plugin in the personal
  profile. The old plugin was repeatedly throwing in its tick and leaving stale
  pedal requests. Competing ACC control channels are excluded.
- Activation acquires fresh steering, pedals, telemetry and a valid route before
  applying control. Missing inputs do not inject an initial brake or pause. A
  short interruption releases the pedals and allows recovery; a persistent one
  disables assistance and reports a manual handover. It never silently resumes
  after that handover.
- The installed map planner can be re-seeded at a road boundary through its own
  factory. A seed must lie within 2.5 m of the truck on a forward road lane. This
  compatibility bridge depends on the installed planner's field/method layout;
  an incompatible version falls back to the normal handover.
- Genuine output-process stalls and unacknowledged braking near an obstacle retain
  an independent emergency brake/pause. Individual plugin freshness failures and
  loss of image capture do not request a pause. The old steering plugin's
  `PauseWhenUnstable` setting is fixed to false.
- Deceleration holds the SDK `motorbrake` input when the engine is running, RPM
  and road speed are sufficient. Acceleration, low RPM and disengagement release
  it. The implementation does not use the engine-brake toggle input.

## Obstacle handling

SDK frames are copied on the publishing thread because the provider reuses its
vehicle and semaphore objects. Planning consequently sees a complete frame. The
native vehicle quaternion is decoded as W/X/Y/Z before projecting the oriented
body onto the road; the public legacy visualization representation is preserved.

A parked vehicle intruding into the actual driving corridor is confirmed after
350 ms. A stopped moving-traffic vehicle needs 1.8 s. Adjacent vehicles, shoulder
objects and different road elevations do not become lane obstructions merely by
being nearby. A vehicle parked in a blocked left lane can also be bypassed on an
available same-direction right lane.

A merge checks the installed planner's spatial transition, articulated trailer
clearance, native traffic coverage and approaching vehicles in the destination
lane. A validated request sets the planner's actual target lane; the planner owns
the curve, indicators and completion. The return waits for the full measured rig
and the connected obstruction zone to clear. A destination gap or collision-free
path is still required; the build does not force a merge through another vehicle.

Native traffic lights constrain speed only when matched to the planned route's
stop line. Toll barriers retain controlled stopping and payment-prompt checks.
Toll OCR, and explicitly enabled visual diagnostics, use the game window surface.
They request Windows' borderless-capture permission. If permission is unavailable,
capture remains off rather than falling back to a yellow-bordered session; an
unreadable toll prompt requires manual payment.

## What the ATS files establish, and the remaining gap

Read-only inspection of `C:\Games\American Truck Simulator\def.scs` on
2026-10-15 found 82 `journey_events_road_event` unit definitions, including the
base parent, and 21 explicit `traffic_block: true` declarations. There are also
nonblocking shoulder events, `centerline_displacement` events and signs placed
100–150 m ahead. Inheritance means those counts are not a complete classification
of all blocking event variants.

These definitions describe event templates. They do not identify which template
is active, its live placement or every cone/concrete collision shape. The
[official SCS telemetry SDK](https://modding.scssoft.com/wiki/Documentation/Engine/SDK/Telemetry)
does not provide that world-obstacle stream. ETS2LA's additional native plugin
provides traffic/parked vehicles and semaphores, but its current output does not
export those roadwork props. See SCS's
[road-event unit documentation](https://modding.scssoft.com/wiki/Documentation/Engine/Units/journey_events_road_event)
for the distinction between templates and runtime instances.

**Cone-only or concrete-only closures remain unsupported by automatic avoidance
when the native stream contains no blocking vehicle.** A low-level road-block
rule was observed during read-only research, but a reliable world-space layout
was not established. Guessed offsets and static event definitions are not used
as live obstacle coordinates. Completing this coverage needs a versioned native
bridge exporting validated active-event bounds/lanes or collision geometry, then
recorded and in-game validation. This build is not proven 100% autonomous.

## Offline verification

- 138 regression tests passed, including native merges without vision, occupied
  destination lanes, trailer geometry, quaternion conversion, snapshot ownership,
  signal stops, readiness/handover and the actual SDK Jake-brake hold/release.
- A second-process probe with isolated test memory maps verified emergency braking
  and SDK pause after a stopped output heartbeat, reset after disable and explicit
  brake-failure escalation while the parent heartbeat remained healthy.
- A Release benchmark of 1,000 iterations with 40 objects and 141 route points
  measured **0.790 ms median / 0.958 ms p95 for two native frames**, including SDK
  snapshot copies, body projection, parked-vehicle confirmation, merge clearance
  and native cruise. This excludes map-route construction, UI rendering and the
  live game; it is not an in-game FPS measurement. The light geometry work runs on
  CPU; optional neural diagnostics retain DirectML GPU execution.
- The user explicitly requested continuing without in-game validation. Acceptance
  by a running game's native planner, real obstacle coverage and the complete toll
  flow have not been verified in this revision.
