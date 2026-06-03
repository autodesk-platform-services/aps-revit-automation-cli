using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitCli.Infrastructure;

public class AppStateStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-cli");

    private static readonly string StatePath = Path.Combine(ConfigDir, "state.json");

    public async Task<AppState?> GetAppStateAsync(string name)
    {
        var states = await LoadAllAsync();
        states.TryGetValue(name, out var state);
        return state;
    }

    public async Task SaveAppStateAsync(string name, int appBundleVersion, string zipHash)
    {
        var states = await LoadAllAsync();
        states[name] = new AppState
        {
            AppBundleVersion = appBundleVersion,
            ZipHash = zipHash
        };

        Directory.CreateDirectory(ConfigDir);

        var json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = StatePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, StatePath, overwrite: true);
    }

    private static async Task<Dictionary<string, AppState>> LoadAllAsync()
    {
        if (!File.Exists(StatePath))
            return new Dictionary<string, AppState>();

        var json = await File.ReadAllTextAsync(StatePath);
        return JsonSerializer.Deserialize<Dictionary<string, AppState>>(json)
               ?? new Dictionary<string, AppState>();
    }

    public class AppState
    {
        [JsonPropertyName("appBundleVersion")]
        public int AppBundleVersion { get; set; }

        [JsonPropertyName("zipHash")]
        public string ZipHash { get; set; } = string.Empty;
    }
}
