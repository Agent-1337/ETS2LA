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
- Both throttle and brake use the native plugin's timestamped direct pedal input
  in this profile. Throttle no longer depends on the virtual controller being
  accepted by the game's focused input path. A live background game receives the
  same controls as a foreground game.
- Activation acquires fresh steering, pedals, telemetry and a valid route before
  applying control. Missing inputs do not inject an initial brake or pause. A
  short controller interruption releases the pedals and allows recovery; missing
  controllers while game frames continue advancing eventually cause a manual
  handover. That handover never silently resumes.
- If the game itself freezes its SDK clocks or pauses, assistance keeps its
  requested state, clears stale pedal/steering inputs and waits without injecting
  a brake/pause command. It reacquires controls automatically after the clocks
  resume. All three SDK clocks are checked, and controls from before a suspension
  cannot satisfy reacquisition. A process check prevents a consumer-held mapping
  from treating an exited game as indefinitely suspended. This cannot make the
  game simulate while the game itself has stopped updating.
- The installed map planner can be re-seeded at a road boundary through its own
  factory. A seed must lie within 2.5 m of the truck on a forward road lane. This
  compatibility bridge depends on the installed planner's field/method layout;
  an incompatible version falls back to the normal handover.
- Genuine output-process stalls and unacknowledged braking near an obstacle retain
  an independent emergency brake/pause. Individual plugin freshness failures and
  loss of image capture do not request a pause. The old steering plugin's
  `PauseWhenUnstable` setting is fixed to false.
- Deceleration holds the SDK `motorbrake` input for at least 3 seconds. Release
  requires 600 ms without braking demand; RPM has separate engage/release
  thresholds (1,100 / 850). Low speed, engine stop, invalid telemetry, suspension
  and disengagement release it immediately. Positive throttle is suppressed while
  the Jake is held; service/emergency braking remains immediate. Cruise also uses
  a speed deadband to avoid repeatedly starting brakes around the target.

## Obstacle handling

SDK frames are copied on the publishing thread because the provider reuses its
vehicle and semaphore objects. Planning consequently sees a complete frame. The
native vehicle quaternion is decoded as W/X/Y/Z before projecting the oriented
body onto the road; the public legacy visualization representation is preserved.

Automatic obstacle passing is disabled in the fixed personal profile. Its
experimental state machine can no longer impose remembered-obstacle stops,
lane-change timeouts or repeated bypass/return attempts. Native cruise still
brakes for vehicles actually intersecting its trajectory, including parked
vehicles. Stationary-vehicle and route-signal stopping distances reach the brake
response guard even while experimental passing is disabled. Vision has no
driving authority. This favors predictable lane following over unvalidated
automatic obstacle bypass.

The retained experimental planner can confirm a parked vehicle intruding into the actual driving corridor after
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

## Wider turns

The tractor's steering path shifts toward the outside before a bend. For a
confirmed tight corner, an additional offset grows during the first part of the
turn: the tractor follows the approach tangent longer, turns later, then retains
the wider exit until the trailer has passed. It does not immediately force the
tractor back into the inside exit lane.
The required extra radius is estimated from the measured axle/hitch spans, body
width and road curvature. A longer articulated combination can need an offset on
a broad bend where a shorter truck needs none. Preparation distance also grows
with rig length and speed; a late activation limits the initial target step.

The offset is capped at 4.5 m (at most one adjacent lane), with the approach
preposition capped at 2 m. Broad bends retain the smaller offset policy. Road lane centers bound the available space on the
same side of circulation; junctions use the more restrictive entry/exit bounds.
Native traffic, closing vehicles and stream coverage restrict any space borrowed
outside the current lane. Missing trailer geometry or traffic coverage retains
only the estimated clearance inside the lane. Unknown road width uses a narrower
fallback. These are map-based lane estimates, not curb or street-furniture scans.

Only Lane Assist's synchronous map sampling sees the adjusted targets. Map
extraction, route selection and lane-change geometry retain their base coordinates;
the existing steering controller follows the new points. The supervisor's displayed
route and cruise curvature checks use the adjusted trajectory. Lane changes,
reverse and manual driving suspend the widening.

Junction progress is projected onto the actual interpolated curves instead of a
straight chord between their ends. This prevents a curved intersection from
misplacing the truck along the steering path. Forward and backward path layouts,
including multiple connected curves, have round-trip regression tests.

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

- 167 regression tests passed, including native merges without vision, occupied
  destination lanes, trailer geometry, quaternion conversion, snapshot ownership,
  signal stops, suspended-game recovery, stale pre-suspension controls, closed
  producers, readiness/handover and the actual SDK Jake-brake hold/release.
- A second-process probe with isolated test memory maps verified emergency braking
  and SDK pause after a stopped output heartbeat, reset after disable and explicit
  brake-failure escalation while the parent heartbeat remained healthy.
- A Release benchmark of 1,000 iterations with 40 objects and 141 route points
  measured **0.790 ms median / 0.958 ms p95 for two native frames**, including SDK
  snapshot copies, body projection, parked-vehicle confirmation, merge clearance
  and native cruise. This excludes map-route construction, UI rendering and the
  live game; it is not an in-game FPS measurement. The light geometry work runs on
  CPU; optional neural diagnostics retain DirectML GPU execution.
- The delayed-turn revision's separate benchmark, also 1,000 iterations with 40
  objects and 141 route points, measured **0.662 ms median / 1.292 ms p95 per
  synthetic frame** for turn planning, native cruise, scoped steering sampling
  and curved-junction projection. It excludes map-route construction, SDK reads,
  the actual steering PID and game physics.
- A simple kinematic trailer model on a 25 m right turn with a 12 m trailer axle
  span reduced the maximum inward axle displacement from **2.85 m to 0 m** in
  this synthetic open-lane case.
  This verifies the geometric effect of the targets; it is not an ATS driving or
  curb-collision measurement. Tests also cover mirrored left turns, early outside
  placement, delayed turn-in, tail clearance, lane boundaries and occupied/closing
  adjacent traffic. The production junction interpolator also round-trips the
  modified targets back to the actual tractor's route progress.
- The user explicitly requested continuing without in-game validation. Acceptance
  by a running game's native planner, real obstacle coverage and the complete toll
  flow have not been verified in this revision.
