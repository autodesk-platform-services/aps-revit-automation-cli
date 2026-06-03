using System.Text.Json.Serialization;

namespace RevitCli.Models.Api;

public class AppBundleResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    [JsonPropertyName("uploadParameters")]
    public UploadParameters? UploadParameters { get; set; }
}

public class UploadParameters
{
    [JsonPropertyName("endpointURL")]
    public string EndpointUrl { get; set; } = string.Empty;

    [JsonPropertyName("formData")]
    public Dictionary<string, string> FormData { get; set; } = new();
}
