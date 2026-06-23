using RevitCli.Infrastructure;
using RevitCli.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RevitCli.Services;

public class YamlConfigService
{
    private static readonly HashSet<string> ValidRevitVersions = ["latest", "2022", "2023", "2024", "2025", "2026", "2027"];
    private static readonly HashSet<string> ValidEnvironments = ["dev", "prod"];
    private static readonly HashSet<string> ValidOpenOptions = ["OpenAllWorksets", "CloseAllWorksets", "CloseWorksetsWithRevitLinks"];

    private readonly CliConfigStore _cliConfigStore;

    public YamlConfigService(CliConfigStore cliConfigStore)
    {
        _cliConfigStore = cliConfigStore;
    }

    public async Task<JobConfig> LoadAsync(string path)
    {
        if (!File.Exists(path))
            throw new ConfigValidationException([$"File not found: {path}"]);

        var yaml = await File.ReadAllTextAsync(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        JobConfig config;
        try
        {
            config = deserializer.Deserialize<JobConfig>(yaml) ?? new JobConfig();
        }
        catch (Exception ex)
        {
            throw new ConfigValidationException([$"YAML parse error: {ex.Message}"]);
        }

        var errors = await ValidateAsync(config);
        if (errors.Count > 0)
            throw new ConfigValidationException(errors);

        return config;
    }

    private async Task<List<string>> ValidateAsync(JobConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Revit.Version))
            errors.Add("'revit.version' is required.");
        else if (!ValidRevitVersions.Contains(config.Revit.Version))
            errors.Add($"'revit.version' must be one of: {string.Join(", ", ValidRevitVersions)}. Got: '{config.Revit.Version}'.");

        if (string.IsNullOrWhiteSpace(config.App.Name))
            errors.Add("'app.name' is required.");
        else if (config.App.Name.Contains('-'))
            errors.Add($"'app.name' must not contain hyphens ('-'); the Design Automation API rejects hyphenated AppBundle ids. Got: '{config.App.Name}'.");

        if (string.IsNullOrWhiteSpace(config.App.Path))
            errors.Add("'app.path' is required.");
        else if (!config.App.Path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            errors.Add("'app.path' must point to a .bundle directory (e.g. './MyPlugin.bundle').");

        if (!string.IsNullOrWhiteSpace(config.Environment) && !ValidEnvironments.Contains(config.Environment))
            errors.Add($"'environment' must be one of: {string.Join(", ", ValidEnvironments)}. Got: '{config.Environment}'.");

        if (string.IsNullOrWhiteSpace(config.Inputs.Model.Type))
            errors.Add("'inputs.model.type' is required.");
        else if (config.Inputs.Model.Type != "cloudWorksharedModel")
            errors.Add($"'inputs.model.type' must be 'cloudWorksharedModel'. Got: '{config.Inputs.Model.Type}'.");

        if (string.IsNullOrWhiteSpace(config.Inputs.Model.FolderUrl))
            errors.Add("'inputs.model.folderUrl' is required.");

        var hasModelName = !string.IsNullOrWhiteSpace(config.Inputs.Model.ModelName);
        var hasModelNames = config.Inputs.Model.ModelNames is { Count: > 0 };
        var hasEmptyModelNames = config.Inputs.Model.ModelNames is not null && config.Inputs.Model.ModelNames.Count == 0;

        if (hasModelName && hasModelNames)
            errors.Add("'inputs.model.modelName' and 'inputs.model.modelNames' are mutually exclusive.");

        if (hasEmptyModelNames)
            errors.Add("'inputs.model.modelNames' must not be empty when provided.");

        if (hasModelNames)
        {
            var maxModels = await _cliConfigStore.GetMaxModelsAsync();
            if (config.Inputs.Model.ModelNames!.Count > maxModels)
                errors.Add($"'inputs.model.modelNames' exceeds the configured max of {maxModels} models. Run 'revit maxmodels <n>' to increase.");
        }

        if (!string.IsNullOrWhiteSpace(config.Inputs.Model.OpenOption) && !ValidOpenOptions.Contains(config.Inputs.Model.OpenOption))
            errors.Add($"'inputs.model.openOption' must be one of: {string.Join(", ", ValidOpenOptions)}. Got: '{config.Inputs.Model.OpenOption}'.");

        if (!string.IsNullOrWhiteSpace(config.Inputs.Tool?.Inputs) && !File.Exists(config.Inputs.Tool.Inputs))
            errors.Add($"'inputs.tool.inputs' file not found: '{config.Inputs.Tool.Inputs}'.");

        var hasOutputType = !string.IsNullOrWhiteSpace(config.Outputs.Result.Type);
        var hasOutputPath = !string.IsNullOrWhiteSpace(config.Outputs.Result.Path);

        if (hasOutputType != hasOutputPath)
        {
            if (!hasOutputType)
                errors.Add("'outputs.result.type' is required when 'outputs.result.path' is set.");
            if (!hasOutputPath)
                errors.Add("'outputs.result.path' is required when 'outputs.result.type' is set.");
        }

        return errors;
    }
}
