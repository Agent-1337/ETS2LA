![](Assets/markdown_logo.png)

# ETS2LA
ETS2LA is a project that aims to finally bring self-driving technology to SCS Software's Truck Simulators. This page includes some information, but if you want to read all the documentation then please head over to [our website](https://ets2la.com)!

<img alt="Discord" src="https://img.shields.io/discord/1120719484982939790?style=for-the-badge&logo=discord"> <img alt="GitHub License" src="https://img.shields.io/github/license/ETS2LA/ETS2LA?style=for-the-badge">


### Important Links
- [Download](https://github.com/ETS2LA/ETS2LA/releases/latest) - Instructions on how to download and install the current version.
- [Discord](https://ets2la.com/discord) - Join our community to talk to the developers and get support.
- [Website](https://ets2la.com) - For troubleshooting and help. *Out of date with the current version!*
- [Docs](https://docs.ets2la.com) - Developer documentation. If you want to work on ETS2LA or plugins you should start here.

### I have a problem with ETS2LA
Please check the [FAQ](https://ets2la.com/help/faq) for answers. If your question is not listed there, please join the [Discord](https://ets2la.com/discord) and ask in the support channel. We will add the answer to the FAQ if it becomes a common question!

### Acknowledgements
- [SCS Software](https://scssoft.com) - For making Euro Truck Simulator 2 and American Truck Simulator.
- [TruckLib](https://github.com/sk-zk/TruckLib) - For being the project we use to extract game files. This version of ETS2LA is fundamentally linked to TruckLib.
- [ts-map](https://github.com/dariowouters/ts-map) and [maps](https://github.com/truckermudgeon/maps) - For their help and examples in parsing SCS map files and rendering a usable map.

---

> [!WARNING]  
> Information below this line is meant for **developers**. If you're a "normal" user then please use the information above.

### Building the Project
For a personal Windows build, double-click `start.bat`. Choose **L** to launch the previous build, **R** to rebuild and launch, or **B** to build only. The first build downloads a local .NET 10 SDK and Bun if needed; the self-contained application is saved to `publish/ETS2LA.exe`. Keep the entire `publish` folder together. A successful rebuild keeps the older build in `publish.previous`; a failed compilation leaves the working build available. Existing plugins and libraries are copied from the previous build or installed app. Build logs are under `.build/`.

You can also run `start.bat --build`, `start.bat --rebuild`, or `start.bat --launch` without the menu.

In **Settings → Game SDK**, use **Choose game folder** or **Choose game executable** to add an installation Steam did not find. Select the root containing `core.scs`, or `bin/win_x64/amtrucks.exe` / `eurotrucks2.exe`, then install the SDKs with the game closed. This location is saved and used for map loading. The game version must have a corresponding bundled SDK.

Full Self-Driving is the only driving mode: **↑ enables**, **↓ disables**. Repeated presses keep the same state; **← and → have no ETS2LA action**. On Windows these two keys work globally while ETS2LA is running, including when minimized or another application has focus. An independent listener combines a Windows hook with key-state polling, so losing DirectInput focus or a hook does not disable the shortcuts. Live status is shown in the sidebar, dashboard, visualization, and control settings. Driving plugins still need to be installed and running. Arrow keys are reserved from other plugin bindings. The old enum values remain in the plugin API for compatibility, but assigning them cannot switch the active mode.

The personal build loads a fixed driving profile on every launch: detected speed limit + 5 km/h, fast acceleration, early collision avoidance, a three-second following gap, and an eight-metre stopping gap. Traffic, curves, and traffic lights can reduce the actual speed below the target. Installed driving and visualization plugins start automatically; driving stays disabled until you enable it. A missing, invalid, or stale planned route blocks throttle and applies braking. Brake requests override throttle, and expired pedal requests are reset in both supported output modes. Existing game paths and display language are preserved. The visualization sidebar overlays the canvas without changing its dimensions. The main window behaves normally and never stays above other windows; older always-on-top preferences are ignored.

These agent-assisted changes are for personal use only; this repository does not accept pull requests containing agent changes.

### Local road awareness (Windows personal build)

The personal build uses a fast, independent orange/white cone and barricade detector at up to five frames per second. A cropped forward-road Grounding DINO model supplies optional semantic confirmation through DirectML, with one job at a time and an adaptive interval of at least 1.5 seconds (about a ten-percent inference duty budget). Input tensors and image buffers are reused; the model is loaded only during active driving and released after thirty seconds idle. Slow neural confirmation never blocks the fast detector or the control loop. Vehicle, road-sign and mirror detections suppress misleading patterns. The cabin navigation HUD is excluded from color-pattern evidence. Current SDK vehicle volumes supply frame-aligned masks, so fast barricade recognition does not depend on an old neural image. Cone boxes extend through the reflective bands to the base. Depth intersects the mapped road grade instead of assuming the entire road is at the tractor's current height. Ground positions are estimated from the game camera and checked against the planned corridor. At least three independent, spatially stable observations over 650 ms are required. Low-confidence predictions and isolated markers do not trigger passing: a marker closure needs three distinct physical markers intruding into the truck's corridor. Roadside concrete boxes cannot become roadblocks through exaggerated bounding-box width. This is an experimental perception system, not comprehensive obstacle coverage.

Keep a forward-facing game camera. Windows Graphics Capture acquires the selected game's own window surface through Direct3D11, including when ETS2LA or another window covers it or has focus. It does not capture desktop pixels or inject into the game. A lightweight worker continuously drains a bounded two-frame GPU pool; inference receives only a fresh frame, cropped to the actual game client. No frame is uploaded or saved by the driving service. Changing focus alone does not cap cruise speed or apply braking. If minimization, exclusive fullscreen, a paused renderer or stale camera data prevents fresh images, new visual maneuvers are suspended while the original telemetry/route controller remains responsible for cruising. Confirmed obstacles and real traffic/route failures still cause appropriate braking; vision loss alone does not abort an already requested clear lane transition. CPU fallback is allowed for detection, but slow/stale predictions cannot authorize new passing. Windows 10 version 1903 or later is required for direct window capture. The visualization's **Road awareness** inset shows confirmed roadwork observations and SDK vehicles; the original map-based 3D view is unchanged.

The planner slows proportionally to a confirmed obstacle's distance rather than forcing every candidate to 20 km/h. A tracker-confirmed closure can initiate a stable lane transition early, without first reducing highway speed to 50 km/h. The merge speed is bounded by the native transition length, road-section availability and lateral acceleration; passing beside the work zone remains capped at 50 km/h. Neural results retain their own capture timestamps and cannot be counted repeatedly as independent observations. It checks same-direction lane geometry and front/rear traffic gaps, then requests the existing Pathfinding lane change via an indicator. It never initiates a crossing into opposing traffic or across a prefab/junction. Its distance requirement includes PathLib's native lane-change length and the trailer; obstacles first detected too close cause a stop rather than an unsafe late maneuver. Passing is capped at 50 km/h while beside the blocked zone. Return remembers the entire connected work zone, including later observations, and waits until the conservative rig length AND measured trailer footprint have cleared the last obstacle by at least 20 metres or two seconds of travel. Trailer telemetry is explicitly enabled; axle positions, attachment, heading and all five supported trailer slots are used, with conservative overhang estimates where the SDK does not expose body dimensions. A 180-metre driven-corridor history preserves curved-road clearance after native Pathfinding drops completed sections. Long/articulated rigs receive additional speed anticipation in tight bends; steering remains the native controller, and this does not guarantee trailer clearance on every hairpin. ACC's centerline tolerance uses the native 2.25-metre default rather than the previous broad three-metre override. A stalled maneuver remains stopped until assistance is disabled and enabled again. Moving queues are followed; stopped vehicles may be passed only with the same checks.

A small second ETS2LA process monitors the actual steering, pedal, route, telemetry and output heartbeat. A previously healthy output stall over 450 ms or stale driving data over 600 ms while moving latches braking and requests the game SDK's pause command. A short initial controller warmup is allowed while the output holds braking. No desktop key or OS pause/shutdown is sent. Pause input is released when acknowledged; recovery requires disabling and re-enabling assistance. Perception slowness or focus changes alone do not trip this guard. The independent brake/pause path was exercised with an 850-ms stalled test parent and isolated test SDK memory maps; this does not prove recovery from an OS or game-engine-wide freeze.

Toll payment requires a fresh, closed gate on the route, a stationary truck, and English/French OCR text explicitly identifying an Enter-to-pay toll prompt. The service pulses the SDK's `activate` command (so it follows the game's interaction binding), never sends global Enter, and waits for payment/gate confirmation. Attempts are bounded to two and a payment acknowledgment is latched for that gate. Ferry, cargo, repair, fuel, and ordinary menu dialogs are not auto-confirmed.

`start.bat` installs the pinned perception assets once (about 720 MB plus language data) under `.tools/perception`, verifies their SHA-256 checksums, and copies them into `publish/Assets/Perception`. Native GPU window capture was checked against an occluded test window, duplicate-frame rejection and resizing. Model/runtime loading, supplied-image detection and synthetic payment OCR also have local verification. The user reported successful work-zone bypass and return with the prior build; the revised long-rig clearance, braking thresholds and toll crossing still need in-game validation. For an already running application, `powershell -File scripts/build.ps1 -StageOnly` prepares `.build/optimized-publish/ETS2LA.exe` without closing or replacing that instance. Close the old instance before starting the staged executable; a normal `start.bat` rebuild still targets `publish`. UI/backend regression tests run with `dotnet test ETS2LA.Tests/ETS2LA.Tests.csproj`.

Download .NET 10 from Microsoft. You can find that [here](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). You'll also have to install [Bun](https://bun.sh/).

Now clone the repository and open the solution file in your preferred IDE. We recommend [VSCode](https://code.visualstudio.com). Any code editor should work, but the project comes prepackaged with VSCode tasks for Windows and Linux. You should also download the `C# Dev Kit` extension, other IDEs should have their own equivalent extensions.

Before you can run ETS2LA, you'll need to make sure you have all the required dotnet tools installed. To install them, just run `dotnet restore` and `dotnet tool restore` in the **root** of the repository.

Building (and running) depends on your IDE. If you're using VSCode, you just press `F5`.

### Testing Translations
Weblate will commit translations every so often, usually within a few hours. However if you want to test your translations immediately, you can:
1. Click your language
2. At the top, click `Files`, then `Download original translation files as ZIP file`
3. Copy `ets2la/ets2la/ETS2LA.Translations/Localization/lang_code.po` into your local `ETS2LA.Translations/Localization/lang_code.po` folder.
4. Build ETS2LA, your new translations will be included.

You can also directly edit the translations files in `ETS2LA.Translations/Localization/`. **However**, do not upload those back to weblate. You can edit the files, and then in weblate edit the text manually. Uploading translation files can (and will) break credits as well as cause the loss of some translations.

### Contributing
Please read CONTRIBUTING.md for guidelines. We welcome all contributions, but please make sure to follow our guidelines to ensure a smooth review process. If you have any questions, feel free to ask in our Discord server. (https://ets2la.com/discord)

---

> [!NOTE]  
> For further information please check the documentation on https://docs.ets2la.com. It's where we gather our knowledgebase.
