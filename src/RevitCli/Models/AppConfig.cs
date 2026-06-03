using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class AppConfig
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }
}
