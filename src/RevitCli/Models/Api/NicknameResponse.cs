using System.Text.Json.Serialization;

namespace RevitCli.Models.Api;

public class NicknameResponse
{
    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;
}
