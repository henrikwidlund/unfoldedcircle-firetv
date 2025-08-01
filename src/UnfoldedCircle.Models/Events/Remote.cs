namespace UnfoldedCircle.Models.Events;

public record Remote
{
    [JsonPropertyName("features")]
    public required RemoteFeature[] Features { get; init; }

    [JsonPropertyName("options")]
    public RemoteOptions? Options { get; set; }
}