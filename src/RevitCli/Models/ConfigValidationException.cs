namespace RevitCli.Models;

public class ConfigValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ConfigValidationException(IReadOnlyList<string> errors)
        : base($"Configuration validation failed with {errors.Count} error(s).")
    {
        Errors = errors;
    }
}
