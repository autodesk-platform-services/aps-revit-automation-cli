using RevitCli.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RevitCli.Services;

public class YamlConfigService
{
    private static readonly HashSet<string> ValidRevitVersions = ["latest", "2022", "2023", "2024", "2025", "2026", "2027"];

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

        var errors = Validate(config);
        if (errors.Count > 0)
            throw new ConfigValidationException(errors);

        return config;
    }

    private static List<string> Validate(JobConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Authentication.ClientId))
            errors.Add("'authentication.clientId' is required.");

        if (string.IsNullOrWhiteSpace(config.Authentication.ClientSecret))
            errors.Add("'authentication.clientSecret' is required.");

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

        if (string.IsNullOrWhiteSpace(config.Inputs.Model.Type))
            errors.Add("'inputs.model.type' is required.");
        else if (config.Inputs.Model.Type != "cloudWorksharedModel")
            errors.Add($"'inputs.model.type' must be 'cloudWorksharedModel'. Got: '{config.Inputs.Model.Type}'.");

        if (string.IsNullOrWhiteSpace(config.Inputs.Model.FolderUrl))
            errors.Add("'inputs.model.folderUrl' is required.");

        if (string.IsNullOrWhiteSpace(config.Inputs.Model.ModelName))
            errors.Add("'inputs.model.modelName' is required.");

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
