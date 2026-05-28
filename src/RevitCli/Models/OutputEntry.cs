using YamlDotNet.Serialization;

namespace RevitCli.Models;

public class OutputEntry
{
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }
}
