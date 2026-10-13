using static ETS2LA.Translations.T;

namespace ETS2LA.Controls.Defaults;

public static class DefaultControls
{
    public static ControlDefinition EnableSelfDriving { get; } = new()
    {
        Id = "ETS2LA.Controls.EnableSelfDriving",
        Name = _("Enable Full Self-Driving"),
        Description = _("Press the up arrow to enable Full Self-Driving. Pressing it again keeps it enabled."),
        DefaultKeybind = "Up",
        Type = ControlType.Boolean
    };

    public static ControlDefinition DisableSelfDriving { get; } = new()
    {
        Id = "ETS2LA.Controls.DisableSelfDriving",
        Name = _("Disable Full Self-Driving"),
        Description = _("Press the down arrow to disable Full Self-Driving immediately."),
        DefaultKeybind = "Down",
        Type = ControlType.Boolean
    };

    public static bool IsDrivingControl(string id) => id == EnableSelfDriving.Id || id == DisableSelfDriving.Id;

    public static bool IsReservedArrow(string deviceId, string key) =>
        deviceId.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)
        && (key.Equals("Up", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Down", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Left", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Right", StringComparison.OrdinalIgnoreCase));

    public static void ApplyBindingPolicy(ControlInstance instance)
    {
        if (IsDrivingControl(instance.Definition.Id))
        {
            instance.DeviceId = "Keyboard";
            instance.ControlId = instance.Definition.Id == EnableSelfDriving.Id ? "Up" : "Down";
        }
        else if (IsReservedArrow(instance.DeviceId, instance.ControlId.ToString() ?? ""))
        {
            // Old saved bindings and plugins must not give the arrows a second action.
            instance.DeviceId = "";
            instance.ControlId = "";
        }
    }

    // Retain the old definitions for plugin binary compatibility, without registering them.
    public static ControlDefinition Increase { get; } = new ControlDefinition
    {
        Id = "ETS2LA.Controls.Increase",
        // TRANSLATORS: This is the name of a keybind.
        Name = _("Increase/RES"),
        Description = _("Increases the current target speed by one step. If assists are disabled, this key acts like RES. Meaning it will resume the last set target speed. If a last target speed was not set, this key acts like SET."),
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };

    public static ControlDefinition Decrease { get; } = new ControlDefinition
    {
        Id = "ETS2LA.Controls.Decrease",
        // TRANSLATORS: This is the name of a keybind.
        Name = _("Decrease/SET"),
        Description = _("Decreases the current target speed by one step. If speed control and lane assist are off, this key acts like SET. Meaning it will set the current speed to speedlimit."),
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };

    public static ControlDefinition Cancel { get; } = new ControlDefinition
    {
        Id = "ETS2LA.Controls.Cancel",
        // TRANSLATORS: This is the name of a keybind.
        Name = _("Cancel/MODE"),
        Description = _("Will disable ETS2LA's assists when pressed. If assists are already disabled, this key will switch between driving modes."),
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };

    public static ControlDefinition Next { get; } = new ControlDefinition
    {
        Id = "ETS2LA.Controls.Next",
        // TRANSLATORS: This is the name of a keybind.
        Name = _("Next/OK"),
        Description = _("This key is used to select in UI menus. It also acts as the approval key for any notifications."),
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };
}
