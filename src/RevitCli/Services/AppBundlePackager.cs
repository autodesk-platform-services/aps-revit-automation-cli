using System.IO.Compression;

namespace RevitCli.Services;

public class AppBundlePackager
{
    public async Task<string> PackageAsync(string bundlePath)
    {
        if (!bundlePath.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Path must end with '.bundle': '{bundlePath}'");

        if (!Directory.Exists(bundlePath))
            throw new DirectoryNotFoundException($"Bundle directory not found: '{bundlePath}'");

        var zipPath = Path.Combine(Path.GetTempPath(), $"revit-cli-{Guid.NewGuid():N}.zip");

        await Task.Run(() => ZipFile.CreateFromDirectory(
            bundlePath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true));

        return zipPath;
    }
}
