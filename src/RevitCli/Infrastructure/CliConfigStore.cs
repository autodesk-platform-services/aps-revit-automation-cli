using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitCli.Infrastructure;

public class CliConfigStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".revit-cli");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public async Task<int> GetMaxModelsAsync()
    {
        if (!File.Exists(ConfigPath))
            return 10;

        var json = await File.ReadAllTextAsync(ConfigPath);
        var config = JsonSerializer.Deserialize<CliConfig>(json);

        return config is null || config.MaxModels <= 0 ? 10 : config.MaxModels;
    }

    public async Task SetMaxModelsAsync(int value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Max models must be a positive integer.");

        Directory.CreateDirectory(ConfigDir);

        var config = new CliConfig { MaxModels = value };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public bool ConfigFileExists() => File.Exists(ConfigPath);

    private class CliConfig
    {
        [JsonPropertyName("maxModels")]
        public int MaxModels { get; set; } = 10;
    }
}
