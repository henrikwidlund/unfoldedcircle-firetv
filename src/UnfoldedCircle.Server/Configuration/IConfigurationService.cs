namespace UnfoldedCircle.Server.Configuration;

public interface IConfigurationService
{
    Task<UnfoldedCircleConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<UnfoldedCircleConfigurationItem?> GetConfigurationItemAsync(string ipaddress, CancellationToken cancellationToken = default);
    Task<UnfoldedCircleConfiguration> UpdateConfigurationAsync(UnfoldedCircleConfiguration configuration, CancellationToken cancellationToken = default);
}