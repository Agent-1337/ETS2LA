namespace ETS2LA.Game;

/// <summary>Resolves a game folder, core.scs, or the 64-bit game executable to one installation root.</summary>
public sealed record GameLocation(GameType Type, string RootPath, string ExecutablePath)
{
    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string GetExecutablePath(string rootPath, GameType type)
    {
        string platform = OperatingSystem.IsWindows() ? "win_x64" : "linux_x64";
        string name = type == GameType.EuroTruckSimulator2 ? "eurotrucks2" : "amtrucks";
        if (OperatingSystem.IsWindows()) name += ".exe";
        return Path.Combine(rootPath, "bin", platform, name);
    }

    public static bool TryResolve(string? selectedPath, out GameLocation? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(selectedPath)) return false;

        try
        {
            string path = Path.GetFullPath(selectedPath.Trim().Trim('"'));
            bool isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path)) return false;

            string? folder = isFile ? Path.GetDirectoryName(path) : path;
            // Accept the installation root, bin, and bin/<platform>. Never search an entire disk.
            for (int depth = 0; folder != null && depth < 3; depth++, folder = Directory.GetParent(folder)?.FullName)
            {
                if (!File.Exists(Path.Combine(folder, "core.scs"))) continue;

                foreach (GameType type in Enum.GetValues<GameType>())
                {
                    string executable = GetExecutablePath(folder, type);
                    if (!File.Exists(executable)) continue;
                    if (isFile && !string.Equals(path, executable, PathComparison)
                        && !string.Equals(path, Path.Combine(folder, "core.scs"), PathComparison)) continue;

                    location = new(type, Path.TrimEndingDirectorySeparator(folder), executable);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return false;
    }
}
