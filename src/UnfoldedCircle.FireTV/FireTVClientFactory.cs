using System.Collections.Concurrent;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;

using Microsoft.Extensions.Logging;

namespace UnfoldedCircle.FireTV;

public class FireTvClientFactory(ILogger<FireTvClientFactory> logger)
{
    private readonly ILogger<FireTvClientFactory> _logger = logger;
    private readonly ConcurrentDictionary<FireTvClientKey, DeviceClient> _clients = new();
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

    public async ValueTask<DeviceClient?> TryGetOrCreateClient(FireTvClientKey fireTvClientKey, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(fireTvClientKey, out var client))
            return client;

        await _semaphoreSlim.WaitAsync(cancellationToken);
        try
        {
            var adbClient = new AdbClient();
            string connectResult;
            do
            {
                connectResult = await adbClient.ConnectAsync(fireTvClientKey.IpAddress, fireTvClientKey.Port, cancellationToken);
            } while (!connectResult.StartsWith("already connected to ", StringComparison.InvariantCultureIgnoreCase));

            var deviceData = (await adbClient.GetDevicesAsync(cancellationToken)).FirstOrDefault(x =>
                x.Serial.Equals($"{fireTvClientKey.IpAddress}:{fireTvClientKey.Port}", StringComparison.InvariantCulture));
            var deviceClient = deviceData.CreateDeviceClient();
            _clients[fireTvClientKey] = deviceClient;
            return deviceClient;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to create client {ClientKey}", fireTvClientKey);
            return null;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    public void TryRemoveClient(in FireTvClientKey fireTvClientKey)
    {
        try
        {
            _clients.TryRemove(fireTvClientKey, out _);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to dispose client {ClientKey}", fireTvClientKey);
            throw;
        }
    }

    public void RemoveAllClients() => _clients.Clear();
}