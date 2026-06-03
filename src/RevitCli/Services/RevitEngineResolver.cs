namespace RevitCli.Services;

public class RevitEngineResolver
{
    private const string LatestVersion = "2027";
    private const string DeprecatedVersion = "2022";

    private static readonly HashSet<string> SupportedVersions = ["2022", "2023", "2024", "2025", "2026", "2027"];

    public string Resolve(string version)
    {
        var resolvedYear = string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase)
            ? LatestVersion
            : version;

        if (!SupportedVersions.Contains(resolvedYear))
            throw new ArgumentException(
                $"Unsupported Revit version '{version}'. Supported versions: {string.Join(", ", SupportedVersions)} or 'latest'.");

        return $"Autodesk.Revit+{resolvedYear}";
    }

    public bool IsDeprecationWarning(string version)
    {
        var resolvedYear = string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase)
            ? LatestVersion
            : version;

        return resolvedYear == DeprecatedVersion;
    }
}
