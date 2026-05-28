using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class RevitConfig
{
    [YamlMember(Alias = "version")]
    public string? Version { get; set; }
}
