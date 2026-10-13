using System.Reflection;
using System.Runtime.CompilerServices;
using ETS2LA.Controls;
using ETS2LA.Controls.Defaults;
using ETS2LA.Game;
using ETS2LA.Game.Telemetry;
using ETS2LA.Settings.Global;
using ETS2LA.State;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ETS2LA.Tests;

public sealed class PersonalBuildTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ETS2LA-tests-" + Guid.NewGuid().ToString("N"));

    private string CreateGame(GameType type)
    {
        string folder = Path.Combine(root, type.ToString(), "Game with spaces");
        string executable = GameLocation.GetExecutablePath(folder, type);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "test fixture");
        File.WriteAllText(Path.Combine(folder, "core.scs"), "test fixture");
        return folder;
    }

    [Theory]
    [InlineData(GameType.AmericanTruckSimulator, "root")]
    [InlineData(GameType.AmericanTruckSimulator, "exe")]
    [InlineData(GameType.AmericanTruckSimulator, "bin")]
    [InlineData(GameType.AmericanTruckSimulator, "platform")]
    [InlineData(GameType.AmericanTruckSimulator, "core")]
    [InlineData(GameType.EuroTruckSimulator2, "root")]
    [InlineData(GameType.EuroTruckSimulator2, "exe")]
    [InlineData(GameType.EuroTruckSimulator2, "core")]
    public void GameSelectionsResolveToTheSameRoot(GameType type, string selection)
    {
        string folder = CreateGame(type);
        string executable = GameLocation.GetExecutablePath(folder, type);
        string path = selection switch {
            "exe" => executable, "bin" => Path.Combine(folder, "bin"),
            "platform" => Path.GetDirectoryName(executable)!, "core" => Path.Combine(folder, "core.scs"), _ => folder
        };
        Assert.True(GameLocation.TryResolve(path, out var result));
        Assert.Equal(type, result!.Type);
        Assert.Equal(folder, result.RootPath);
        Assert.Equal(executable, result.ExecutablePath);
    }

    [Fact]
    public void QuotedPathsAndWindowsCaseResolve()
    {
        string folder = CreateGame(GameType.AmericanTruckSimulator);
        string selected = OperatingSystem.IsWindows() ? folder.ToUpperInvariant() : folder;
        Assert.True(GameLocation.TryResolve("\"" + selected + "\"", out var result));
        Assert.True(string.Equals(folder, result!.RootPath, GameLocation.PathComparison));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("does-not-exist")]
    public void MissingOrCancelledPathsAreRejected(string? selected) => Assert.False(GameLocation.TryResolve(selected, out _));

    [Fact]
    public void UnrelatedExecutableAndIncompleteGameAreRejected()
    {
        string folder = CreateGame(GameType.AmericanTruckSimulator);
        string unrelated = Path.Combine(Path.GetDirectoryName(GameLocation.GetExecutablePath(folder, GameType.AmericanTruckSimulator))!, "other.exe");
        File.WriteAllText(unrelated, "test");
        Assert.False(GameLocation.TryResolve(unrelated, out _));
        File.Delete(Path.Combine(folder, "core.scs"));
        Assert.False(GameLocation.TryResolve(folder, out _));
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("Left")]
    [InlineData("Right")]
    public void ExistingPluginArrowBindingsAreCleared(string key)
    {
        var binding = new ControlInstance { Definition = new() { Id = "plugin.mode", Name = "Mode", Description = "" }, DeviceId = "Keyboard", ControlId = key };
        DefaultControls.ApplyBindingPolicy(binding);
        Assert.False(binding.IsBound());
    }

    [Fact]
    public void DrivingKeysAreFixedButWheelButtonsRemainAvailable()
    {
        foreach (var definition in new[] { DefaultControls.EnableSelfDriving, DefaultControls.DisableSelfDriving })
        {
            var binding = new ControlInstance { Definition = definition, DeviceId = "wheel", ControlId = "Button 1" };
            DefaultControls.ApplyBindingPolicy(binding);
            Assert.Equal("Keyboard", binding.DeviceId);
            Assert.Equal(definition.DefaultKeybind, binding.ControlId);
        }
        Assert.False(DefaultControls.IsReservedArrow("wheel", "Up"));
        Assert.False(DefaultControls.IsReservedArrow("Keyboard", "F1"));
    }

    private static ApplicationState CreateState(float limit = 20)
    {
        // Exercise the production state and real control callbacks without starting native SDK readers.
        var state = (ApplicationState)RuntimeHelpers.GetUninitializedObject(typeof(ApplicationState));
        var telemetry = new GameTelemetryData();
        telemetry.truckFloat.speedLimit = limit;
        typeof(ApplicationState).GetField("latestTelemetryData", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, telemetry);
        typeof(ApplicationState).GetField("stateSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, new StateSettings());
        AssistanceSettings.Current.MaximumSpeed = 0;
        return state;
    }

    private static ControlInstance Bind(ApplicationState state, ControlDefinition definition, string method)
    {
        var binding = new ControlInstance { Definition = definition };
        var callback = typeof(ApplicationState).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<EventHandler<ControlChangeEventArgs>>(state);
        binding.RegisterCallback(callback);
        return binding;
    }

    [Fact]
    public void UpEnablesOnPressAndNeverChangesSpeedWhileEnabledDownDisablesOnPress()
    {
        var state = CreateState();
        var up = Bind(state, DefaultControls.EnableSelfDriving, "HandleEnableSelfDriving");
        var down = Bind(state, DefaultControls.DisableSelfDriving, "HandleDisableSelfDriving");
        int changes = 0;
        state.DrivingStatusChanged += (sender, args) => changes++;
        up.UpdateState(true);
        Assert.True(state.EnableAssists);
        Assert.Equal(ApplicationState.SpeedTargetForLimit(20), state.DesiredSpeed, 4);
        up.UpdateState(false);
        up.UpdateState(true);
        Assert.True(state.EnableAssists);
        Assert.Equal(ApplicationState.SpeedTargetForLimit(20), state.DesiredSpeed, 4);
        Assert.Equal(1, changes);
        down.UpdateState(true);
        Assert.False(state.EnableAssists);
        down.UpdateState(false);
        Assert.False(state.EnableAssists);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(DrivingMode.AdaptiveCruiseControlOnly)]
    [InlineData(DrivingMode.LaneAssistOnly)]
    [InlineData((DrivingMode)999)]
    public void PluginsCannotSwitchAwayFromFullSelfDriving(DrivingMode other)
    {
        var state = CreateState();
        state.DrivingMode = other;
        Assert.Equal(DrivingMode.FullSelfDriving, state.DrivingMode);
    }

    [Fact]
    public void EnablingUsesFixedOffsetInsteadOfOldMaximumSpeedAndHasAMissingLimitFallback()
    {
        var state = CreateState(30);
        AssistanceSettings.Current.MaximumSpeed = 10;
        state.SetSelfDrivingEnabled(true);
        Assert.Equal(ApplicationState.SpeedTargetForLimit(30), state.DesiredSpeed, 4);
        state.SetSelfDrivingEnabled(false);
        state = CreateState(0);
        state.SetSelfDrivingEnabled(true);
        Assert.Equal(ApplicationState.SpeedTargetForLimit(new StateSettings().FallbackSpeed), state.DesiredSpeed, 4);
    }

    [Fact]
    public void SdkFilesGoNextToSelectedExecutableAndPartialCopiesDoNotCountAsInstalled()
    {
        string folder = CreateGame(GameType.AmericanTruckSimulator);
        var installation = new Installation {
            Type = GameType.AmericanTruckSimulator, Path = folder, DocumentsPath = root,
            ExecutablePath = GameLocation.GetExecutablePath(folder, GameType.AmericanTruckSimulator), Version = "1.60"
        };
        Assert.True(installation.HasBundledSDK("1.60"));
        Assert.False(installation.IsSDKInstalled("1.60"));
        Assert.True(installation.InstallSDK("1.60"));
        Assert.True(installation.IsSDKInstalled("1.60"));
        string library = Path.Combine(installation.SDKDirectory, "ets2la_plugin.dll");
        Assert.True(File.Exists(library));
        File.Delete(library);
        Assert.False(installation.IsSDKInstalled("1.60"));
        Assert.True(installation.UninstallSDK("1.60"));
        Assert.False(File.Exists(Path.Combine(installation.SDKDirectory, "scs-telemetry.dll")));
        Assert.True(File.Exists(installation.ExecutablePath));
    }

    [Fact]
    public void DisconnectingTheGameDisablesDriving()
    {
        var state = CreateState();
        state.IsGameRunning = true;
        state.EnableAssists = true;
        typeof(ApplicationState).GetMethod("HandleTelemetryUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(state, new object[] { new GameTelemetryData { sdkActive = false } });
        Assert.False(state.EnableAssists);
        Assert.False(state.IsGameRunning);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
