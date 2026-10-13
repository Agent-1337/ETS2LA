namespace ETS2LA.Controls.Windows;

/// <summary>Merges hook events and sampled key state without duplicate presses or stuck releases.</summary>
internal sealed class DrivingKeyTransitions(Action<string, bool> changed, bool up = false, bool down = false)
{
    private bool upPressed = up;
    private bool downPressed = down;
    private long lastUpHook = -1000;
    private long lastDownHook = -1000;

    public void FromHook(string key, bool pressed, long now)
    {
        if (key == "Up") lastUpHook = now;
        else if (key == "Down") lastDownHook = now;
        else return;
        Update(key, pressed);
    }

    public void Poll(bool up, bool down, long now)
    {
        // A hook fires before the asynchronous state changes. Allow that state
        // to settle rather than interpreting the old sample as a second edge.
        if (now - lastUpHook >= 30) Update("Up", up);
        if (now - lastDownHook >= 30) Update("Down", down);
    }

    private void Update(string key, bool pressed)
    {
        ref bool previous = ref (key == "Up" ? ref upPressed : ref downPressed);
        if (previous == pressed) return;
        previous = pressed;
        changed(key, pressed);
    }
}
