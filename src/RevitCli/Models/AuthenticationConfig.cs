using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class AuthenticationConfig
{
    [YamlMember(Alias = "clientId")]
    public string? ClientId { get; set; }

    [YamlMember(Alias = "clientSecret")]
    public string? ClientSecret { get; set; }
}
