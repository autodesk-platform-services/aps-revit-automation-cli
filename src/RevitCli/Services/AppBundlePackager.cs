using System.IO.Compression;
using System.Security.Cryptography;

namespace RevitCli.Services;

public class AppBundlePackager
{
    public async Task<(string ZipPath, string Sha256Hex)> PackageAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"App folder not found: '{folderPath}'");

        var bundleFolders = Directory.GetDirectories(folderPath, "*.bundle");
        if (bundleFolders.Length == 0)
            throw new InvalidOperationException(
                $"No .bundle subfolder found in '{folderPath}'. The app folder must contain exactly one .bundle directory.");
        if (bundleFolders.Length > 1)
            throw new InvalidOperationException(
                $"Multiple .bundle subfolders found in '{folderPath}'. The app folder must contain exactly one .bundle directory.");

        var zipPath = Path.Combine(Path.GetTempPath(), $"revit-cli-{Guid.NewGuid():N}.zip");

        await Task.Run(() => ZipFile.CreateFromDirectory(folderPath, zipPath));

        var sha256Hex = await ComputeSha256Async(zipPath);

        return (zipPath, sha256Hex);
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
