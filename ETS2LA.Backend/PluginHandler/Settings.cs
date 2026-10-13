using ETS2LA.Settings;
using ETS2LA.Logging;
using static ETS2LA.Translations.T;

namespace ETS2LA.Backend.Plugins;

[Serializable]
public enum PluginType
{
    Plugin,
    Library
}

[Serializable]
public struct InstalledPlugin
{
    public string Id { get; set; }
    public string Version { get; set; }
    public string DllPath { get; set; }
    public List<string> Dependencies { get; set; }
    public PluginType Type { get; set; }
}

[Serializable]
public class InstalledPluginManifest
{
    [NonSerialized]
    private static readonly Lazy<InstalledPluginManifest> _instance = new(() => new InstalledPluginManifest(loadSettings: true));
    public static InstalledPluginManifest Current => _instance.Value;
    /// ---
    
    public List<InstalledPlugin> InstalledPlugins { get; set; } = new List<InstalledPlugin>();

    /// ---
    [NonSerialized]
    private SettingsHandler? _settingsHandler;

    public InstalledPluginManifest(bool loadSettings = false)
    {
        if (loadSettings)
        {
            _settingsHandler = new SettingsHandler();
            var loadedSettings = _settingsHandler.Load<InstalledPluginManifest>("InstalledPluginManifest.json");
            if (loadedSettings != null)
            {
                InstalledPlugins = loadedSettings.InstalledPlugins;

                // Check if the plugins actually exist
                int index = 0;
                while (index < InstalledPlugins.Count)
                {
                    var plugin = InstalledPlugins[index];
                    if (!File.Exists(plugin.DllPath))
                    {
                        // A portable/local build may use a different application folder.
                        // Keep the installation record so returning to the installed app
                        // or restoring its files does not lose the user's plugins.
                        Logger.Warn(_("Plugin '{0}' is missing its DLL at '{1}'. Its installation record has been kept.", plugin.Id ?? "Unknown", plugin.DllPath ?? "Unknown"));
                        index++;
                    }
                    else
                    {
                        index++;
                    }
                }

            }
            _settingsHandler.RegisterListener<InstalledPluginManifest>("InstalledPluginManifest.json", OnSettingsChanged);
        }
    }

    public InstalledPluginManifest() { }

    public void Save()
    {
        _settingsHandler?.Save<InstalledPluginManifest>("InstalledPluginManifest.json", this);
    }

    public void OnSettingsChanged(InstalledPluginManifest newSettings)
    {
        InstalledPlugins = newSettings.InstalledPlugins;
    }
}
