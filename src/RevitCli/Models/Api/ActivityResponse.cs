using System.Text.Json.Serialization;

namespace RevitCli.Models.Api;

public class ActivityResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;
}
