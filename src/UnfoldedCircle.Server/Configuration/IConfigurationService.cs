using UnfoldedCircle.Models.Sync;

namespace UnfoldedCircle.Server.Configuration;

public interface IConfigurationService
{
    Task<UnfoldedCircleConfiguration> GetConfigurationAsync(CancellationToken cancellationToken);
    Task<UnfoldedCircleConfigurationItem?> GetConfigurationItemAsync(string macAddress, CancellationToken cancellationToken);
    Task<UnfoldedCircleConfiguration> UpdateConfigurationAsync(UnfoldedCircleConfiguration configuration, CancellationToken cancellationToken);
    ValueTask<DriverMetadata> GetDriverMetadataAsync(CancellationToken cancellationToken);
}