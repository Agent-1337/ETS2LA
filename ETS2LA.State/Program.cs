using ETS2LA.Controls;
using ETS2LA.Controls.Defaults;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Telemetry;
using ETS2LA.Settings.Global;
using ETS2LA.Settings;
using ETS2LA.Game;
using ETS2LA.Game.SiiFiles;
using ETS2LA.Game.PpdFiles;
using ETS2LA.Logging;
using ETS2LA.Notifications;
using static ETS2LA.Translations.T;
using ETS2LA.Game.PmdFiles;

namespace ETS2LA.State;

public enum DrivingMode
{
    AdaptiveCruiseControlOnly,
    FullSelfDriving,
    LaneAssistOnly
}

/// <summary>
///  This state contains the most important ETS2LA variables. Most plugins
///  will use it to follow the user's preferences and read the game data.
/// </summary>
public class ApplicationState
{
    private static readonly Lazy<ApplicationState> _instance = new(() => new ApplicationState());
    public static ApplicationState Current => _instance.Value;
    private volatile bool shutdown = false;

    public ApplicationState()
    {
        assistanceSettings = AssistanceSettings.Current;
        HandleSettingsChanged(StateSettingsHandler.Current.GetSettings());
        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, HandleTelemetryUpdate);
        Events.Current.Subscribe<float>("TelemetryEvents.SpeedLimitChanged", HandleSpeedLimitChanged);

        ControlsBackend.Current.On(DefaultControls.EnableSelfDriving.Id, HandleEnableSelfDriving);
        ControlsBackend.Current.On(DefaultControls.DisableSelfDriving.Id, HandleDisableSelfDriving);
        Events.Current.Subscribe<string>("ETS2LA.Game.Output.SafetyStatus", reason => {
            DrivingSafetyReason = reason;
            DrivingStatusChanged?.Invoke(this, EventArgs.Empty);
        });

        assistanceSettings = AssistanceSettings.Current;

        StateSettingsHandler.Current.OnSettingsChanged += HandleSettingsChanged;
        HandleSettingsChanged(StateSettingsHandler.Current.GetSettings());
    }

    private void HandleSettingsChanged(StateSettings newStateSettings)
    {
        stateSettings = newStateSettings;
        DisplayUnits = newStateSettings.DisplayUnits;
    }

    private void HandleTelemetryUpdate(GameTelemetryData data)
    {
        latestTelemetryData = data;
        if (data.sdkActive)
        {
            UpdateSpeedTarget(data.truckFloat.speedLimit);
            IsGameRunning = true;
            RunningGameType = data.scsValues.game == "ETS2" ? GameType.EuroTruckSimulator2
                                                            : GameType.AmericanTruckSimulator;
            RunningGameVersion = data.scsValues.versionMajor.ToString() + "."
                               + data.scsValues.versionMinor.ToString();

            if (RunningGame != null && RunningGame.Type != RunningGameType)
            {
                Logger.Info(_("Detected a switch to {0}, waiting for its game data to be parsed.", RunningGameType));
                Installation oldGame = RunningGame;
                RunningGame = null;
                // Unload in the background so the forced GC doesn't
                // stall the telemetry thread.
                Task.Run(oldGame.ClearParsedData);
            }

            if (RunningGame == null && (parsingTask == null || parsingTask.IsCompleted))
                // This function will run until game data is successfully parsed.
                parsingTask = WaitForParseSuccessful();
        }
        else
        {
            if (IsGameRunning) { EnableAssists = false; lastSpeedLimit = 0; DesiredSpeed = 0; }
            IsGameRunning = false;
        }
    }

    public void Shutdown()
    {
        shutdown = true;
    }
    


    // MARK: Self-Driving Related
    // NOTE: This class is organized by *category* and not variable/function type.
    //       This makes the most sense to avoid having lots of variables back to back
    //       far from the relevant functions. It is slightly unconventional though.
    //       Follow the marks :+1:



    /// <summary>
    ///  Full Self-Driving is the sole mode. Keep the setter and enum values for plugin compatibility.
    /// </summary>
    public DrivingMode DrivingMode { get => DrivingMode.FullSelfDriving; set { } }
    public Dictionary<DrivingMode, string> DrivingModeTranslation = new Dictionary<DrivingMode, string>
    {
        { DrivingMode.FullSelfDriving, _("Full Self-Driving") }
    };

    /// <summary>
    ///  This value will determine whether assists are enabled or not.
    /// </summary>
    private volatile bool enableAssists;
    public string DrivingSafetyReason { get; private set; } = "";
    public event EventHandler? DrivingStatusChanged;
    public bool EnableAssists
    {
        get => enableAssists;
        set
        {
            if (enableAssists == value) return;
            enableAssists = value;
            Events.Current.Publish("ETS2LA.State.AssistsEnabled", value);
            DrivingStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///  This value will be used by the longitudinal assist to determine the target speed. This value does
    ///  not take into account any environmental factors. That will either be provided by plugins, or the
    ///  user will have to take care of it themselves. <br/><br/>
    ///  **This value is in scientific units, that is m/s.**
    /// </summary>
    public float DesiredSpeed { get; set; } = 0.0f;

    /// <summary>
    ///  This value will determine the currently used units for any values shown in the UI. This value
    ///  is automatically changed by ETS2LA, either when the user sets it in the settings, or when we
    ///  detect a change in the game's units. This unit should determine the units used everywhere, e.g.
    ///  the units used when increasing and decreasing the target speed. (+-1 mph/kph/ms) <br/><br/>
    ///  **Use UnitConversions.FromScientificUnits and UnitConversions.ToScientificUnits to convert values to and from the current display units.**
    /// </summary>
    public Units DisplayUnits { get; set; } = Units.Metric;
    public Dictionary<Units, string> DisplayUnitsTranslation = new Dictionary<Units, string>
    {
        { Units.Metric, _("Metric") },
        { Units.Imperial, _("Imperial") },
        { Units.Scientific, _("Scientific") }
    };

    // Internal value to keep track of the latest telemetry we received.
    private GameTelemetryData latestTelemetryData = new();
    private AssistanceSettings assistanceSettings;
    private StateSettings stateSettings;
    private float lastSpeedLimit = 0f;

    // The functions below are for handling control events.
    // If determining what they do is hard via code, then take a look at the 
    // example at https://docs.ets2la.com/docs/Rewrite/UserInput#how-to-listen-to-registered-controls

    public static float SpeedTargetForLimit(float speedLimit) => speedLimit + PersonalDrivingProfile.SpeedOffsetMetresPerSecond;

    private void UpdateSpeedTarget(float newSpeedLimit)
    {
        if (float.IsFinite(newSpeedLimit) && newSpeedLimit > 0) lastSpeedLimit = newSpeedLimit;
        float limit = lastSpeedLimit > 0 ? lastSpeedLimit : stateSettings.FallbackSpeed;
        DesiredSpeed = SpeedTargetForLimit(limit);
    }

    private void HandleSpeedLimitChanged(float newSpeedLimit)
    {
        UpdateSpeedTarget(newSpeedLimit);
        if (!EnableAssists) return;
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = "ApplicationState.SpeedLimitChanged",
            Title = _("Speed limit changed"),
            Content = _("Target: {0} {1}", (int)Math.Round(UnitConversions.FromScientificUnits(UnitType.Speed, DesiredSpeed, DisplayUnits), 0), UnitConversions.GetUnitAbbreviation(UnitType.Speed, DisplayUnits))
        });
    }

    private void HandleEnableSelfDriving(object? sender, ControlChangeEventArgs e)
    {
        if (e.NewValue is true) SetSelfDrivingEnabled(true);
    }

    private void HandleDisableSelfDriving(object? sender, ControlChangeEventArgs e)
    {
        if (e.NewValue is true) SetSelfDrivingEnabled(false);
    }

    public void SetSelfDrivingEnabled(bool enabled)
    {
        if (EnableAssists == enabled) return;
        if (enabled)
        {
            UpdateSpeedTarget(latestTelemetryData.truckFloat.speedLimit);
        }

        EnableAssists = enabled;
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = "ApplicationState.SelfDrivingStatus",
            Title = enabled ? _("Full Self-Driving enabled") : _("Full Self-Driving disabled"),
            Content = enabled ? _("Target speed: current limit + 5 km/h.") : _("Manual driving."),
            CloseAfter = 3
        });
    }

    // MARK: Map Data Related



    public bool IsGameRunning { get; set; } = false;
    public GameType? RunningGameType { get; set; }
    public string? RunningGameVersion { get; set; }
    private Task? parsingTask;
    public Installation? RunningGame { get; set; }

    private async Task WaitForParseSuccessful()
    {
        while (!shutdown)
        {
            foreach(Installation install in GameHandler.Current.Installations)
            {
                if (RunningGame != null)
                    break;

                if(install.Type == RunningGameType)
                {
                    bool success = await Task.Run(() => install.Parse());
                    // The user might have switched games while we were parsing,
                    // in that case throw the now stale data away.
                    if (success && install.Type == RunningGameType)
                    {
                        RunningGame = install;
                        SiiFileHandler.Current.SetFileSystem(RunningGame.GetFileSystem());
                        PpdFileHandler.Current.SetFileSystem(RunningGame.GetFileSystem());
                        PmdFileHandler.Current.SetFileSystem(RunningGame.GetFileSystem());
                    }
                    else if (success)
                        install.ClearParsedData();
                }
            }

            if (RunningGame != null && RunningGame.Type == RunningGameType)
                break;

            await Task.Delay(5000);
        }
    }
}
