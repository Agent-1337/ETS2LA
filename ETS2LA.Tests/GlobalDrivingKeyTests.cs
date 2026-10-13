using ETS2LA.Controls.Windows;
using Xunit;

namespace ETS2LA.Tests;

public sealed class GlobalDrivingKeyTests
{
    [Fact]
    public void HookAndPollingDeliverOnePressAndReleasePerKey()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)));
        keys.FromHook("Up", true, 100);
        keys.FromHook("Up", true, 110); // auto repeat
        keys.Poll(true, false, 150);
        keys.FromHook("Up", false, 160);
        keys.Poll(false, false, 200);
        keys.FromHook("Down", true, 210);
        keys.Poll(false, true, 250);
        keys.FromHook("Down", false, 260);
        Assert.Equal(new[] { ("Up", true), ("Up", false), ("Down", true), ("Down", false) }, events);
    }

    [Fact]
    public void HookPressBeforeWindowsAsyncStateChangesIsNotReleasedOrRepeated()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)));
        keys.FromHook("Up", true, 100);
        keys.Poll(false, false, 101); // Windows still reports the old state
        keys.Poll(true, false, 140);
        keys.FromHook("Up", false, 150);
        keys.Poll(true, false, 151); // release has not reached async state yet
        keys.Poll(false, false, 190);
        Assert.Equal(new[] { ("Up", true), ("Up", false) }, events);
    }

    [Fact]
    public void PollingRecoversMissingHookReleaseAndSubsequentPresses()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)));
        keys.FromHook("Up", true, 100);
        // A removed hook will supply neither the release nor any subsequent events.
        keys.Poll(false, false, 200);
        keys.Poll(true, false, 250);
        keys.Poll(false, false, 300);
        keys.Poll(false, true, 350);
        keys.Poll(false, false, 400);
        Assert.Equal(new[] { ("Up", true), ("Up", false), ("Up", true), ("Up", false), ("Down", true), ("Down", false) }, events);
    }

    [Fact]
    public void PollingWorksWhenHookCannotBeInstalled()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)));
        keys.Poll(true, false, 10);
        keys.Poll(true, false, 20);
        keys.Poll(false, false, 30);
        keys.Poll(false, true, 40);
        Assert.Equal(new[] { ("Up", true), ("Up", false), ("Down", true) }, events);
    }

    [Fact]
    public void ShortPressesArePreservedEvenWhenBothHookEventsArriveBeforePoll()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)));
        keys.FromHook("Up", true, 100);
        keys.FromHook("Up", false, 100);
        keys.Poll(false, false, 150);
        Assert.Equal(new[] { ("Up", true), ("Up", false) }, events);
    }

    [Fact]
    public void KeysHeldAtStartupRequireANewPressAndOtherArrowsAreIgnored()
    {
        var events = new List<(string, bool)>();
        var keys = new DrivingKeyTransitions((key, down) => events.Add((key, down)), up: true);
        keys.Poll(true, false, 100);
        keys.FromHook("Left", true, 110);
        keys.FromHook("Right", true, 120);
        Assert.Empty(events);
        keys.Poll(false, false, 150);
        keys.Poll(true, false, 200);
        Assert.Equal(new[] { ("Up", false), ("Up", true) }, events);
    }
}
