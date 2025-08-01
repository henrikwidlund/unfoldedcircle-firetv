namespace UnfoldedCircle.Server.Configuration;

public record UnfoldedCircleConfigurationItem
{
    public required string IpAddress { get; init; }
    public required string MacAddress { get; init; }
    public required int Port { get; init; }
    public string? DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string EntityId { get; init; }
}