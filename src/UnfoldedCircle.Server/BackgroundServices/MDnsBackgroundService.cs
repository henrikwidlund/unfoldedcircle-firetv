using Makaretu.Dns;

using UnfoldedCircle.Server.AdbTv;
using UnfoldedCircle.Server.Configuration;

namespace UnfoldedCircle.Server.BackgroundServices;

public sealed class MDnsBackgroundService : IHostedService, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProfile _serviceProfile;
    private ServiceDiscovery? _serviceDiscovery;

    public MDnsBackgroundService(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        // Get the local hostname
        _serviceProfile = new ServiceProfile(AdbTvConstants.DriverId,
            "_uc-integration._tcp",
            configuration.GetOrDefault<ushort>("UC_INTEGRATION_HTTP_PORT", 9001))
        {
            HostName = $"{System.Net.Dns.GetHostName().Split('.')[0]}.local"
        };

        // Add TXT records
        _serviceProfile.AddProperty("name", AdbTvConstants.DriverName);
        _serviceProfile.AddProperty("ver", AdbTvConstants.DriverVersion);
        _serviceProfile.AddProperty("developer", AdbTvConstants.DriverDeveloper);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceDiscovery = await ServiceDiscovery.CreateInstance(loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
        _serviceDiscovery.Advertise(_serviceProfile);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_serviceDiscovery is not null)
            await _serviceDiscovery.Unadvertise(_serviceProfile);
    }

    public void Dispose() => _serviceDiscovery?.Dispose();
}