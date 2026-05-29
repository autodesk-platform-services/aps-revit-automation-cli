using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RevitCli.Infrastructure;

namespace RevitCli.Services;

public class OssService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OssService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task CreateBucketAsync(string bucketKey, string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");

        var payload = new { bucketKey, policyKey = "transient" };
        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/oss/v2/buckets")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync($"create OSS bucket '{bucketKey}'");
    }

    public string GetObjectUrn(string bucketKey, string objectName)
    {
        return $"urn:adsk.objects:os.object:{bucketKey}/{objectName}";
    }

    public async Task DownloadObjectAsync(string bucketKey, string objectName, string localPath, string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");

        var encodedObjectName = Uri.EscapeDataString(objectName);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/oss/v2/buckets/{bucketKey}/objects/{encodedObjectName}/signeds3download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

        var response = await client.SendAsync(request);
        await response.EnsureSuccessOrThrowAsync($"get OSS signed download URL for '{objectName}'");

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var signedUrl = doc.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Signed download URL was null.");

        var parentDir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        using var downloadClient = new HttpClient();
        using var downloadStream = await downloadClient.GetStreamAsync(signedUrl);
        using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write);
        await downloadStream.CopyToAsync(fileStream);
    }

    public async Task DeleteBucketAsync(string bucketKey, string twoLeggedToken)
    {
        var client = _httpClientFactory.CreateClient("aps");

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/oss/v2/buckets/{bucketKey}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twoLeggedToken);

        var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await response.EnsureSuccessOrThrowAsync($"delete OSS bucket '{bucketKey}'");
    }
}
