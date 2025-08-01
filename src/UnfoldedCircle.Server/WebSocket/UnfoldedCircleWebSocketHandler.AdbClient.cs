using System.Globalization;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;

using UnfoldedCircle.FireTV;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Server.Configuration;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private async Task<FireTvClientKey?> TryGetFireTvClientKey(
        string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        if (configuration.Entities.Count == 0)
        {
            _logger.LogInformation("[{WSId}] WS: No configurations found", wsId);
            return null;
        }

        var entity = !string.IsNullOrWhiteSpace(deviceId)
            ? configuration.Entities.Find(x => string.Equals(x.DeviceId, deviceId, StringComparison.Ordinal))
            : configuration.Entities[0];
        if (entity is not null)
            return new FireTvClientKey(entity.IpAddress, entity.MacAddress, entity.Port);

        _logger.LogInformation("[{WSId}] WS: No configuration found for device ID '{DeviceId}'", wsId, deviceId);
        return null;
    }
    
    private async Task<FireTvClientHolder?> TryGetFireTvClientHolder(
        string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var fireTvClientKey = await TryGetFireTvClientKey(wsId, deviceId, cancellationToken);
        if (fireTvClientKey is null)
            return null;
        
        var deviceClient = await _fireTvClientFactory.TryGetOrCreateClient(fireTvClientKey.Value, cancellationToken);
        if (deviceClient is null)
            return null;

        if (deviceClient.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online)
            return new FireTvClientHolder(deviceClient, fireTvClientKey.Value);

        _fireTvClientFactory.TryRemoveClient(fireTvClientKey.Value);
        deviceClient = await _fireTvClientFactory.TryGetOrCreateClient(fireTvClientKey.Value, cancellationToken);

        return deviceClient is null ? null : new FireTvClientHolder(deviceClient, fireTvClientKey.Value);
    }

    private async Task<bool> CheckClientApproved(string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var fireTvClientKey = await TryGetFireTvClientKey(wsId, deviceId, cancellationToken);
        if (fireTvClientKey is null)
            return false;

        var adbClient = new AdbClient();
        string connectResult;
        do
        {
            connectResult = await adbClient.ConnectAsync(fireTvClientKey.Value.IpAddress, fireTvClientKey.Value.Port, cancellationToken);
        } while (!connectResult.StartsWith("already connected to ", StringComparison.InvariantCultureIgnoreCase));

        var deviceData = (await adbClient.GetDevicesAsync(cancellationToken)).FirstOrDefault(x =>
            x.Serial.Equals($"{fireTvClientKey.Value.IpAddress}:{fireTvClientKey.Value.Port}", StringComparison.InvariantCulture));
        return deviceData is { State: AdvancedSharpAdbClient.Models.DeviceState.Online };
    }
    
    private async Task<bool> TryDisconnectAdbClient(
        string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var fireTvClientKey = await TryGetFireTvClientKey(wsId, deviceId, cancellationToken);
        if (fireTvClientKey is null)
            return false;
        
        _fireTvClientFactory.TryRemoveClient(fireTvClientKey.Value);
        return true;
    }
    
    private async Task<UnfoldedCircleConfigurationItem> UpdateConfiguration(
        Dictionary<string, string> msgDataSetupData,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        var ipAddress = msgDataSetupData[FireTv.FireTvConstants.IpAddressKey];
        var macAddress = msgDataSetupData[FireTv.FireTvConstants.MacAddressKey];
        var deviceId = msgDataSetupData.GetValueOrDefault(FireTv.FireTvConstants.DeviceIdKey, ipAddress);
        var port = msgDataSetupData.TryGetValue(FireTv.FireTvConstants.PortKey, out var portValue)
            ? int.Parse(portValue, NumberFormatInfo.InvariantInfo)
            : 5555;

        var entity = configuration.Entities.Find(x => string.Equals(x.DeviceId, deviceId, StringComparison.Ordinal));
        if (entity is null)
        {
            _logger.LogInformation("Adding configuration for device ID '{DeviceId}'", deviceId);
            entity = new UnfoldedCircleConfigurationItem
            {
                IpAddress = ipAddress,
                MacAddress = macAddress,
                Port = port,
                DeviceId = deviceId,
                DeviceName = $"{FireTv.FireTvConstants.DeviceName} {ipAddress}",
                EntityId = FireTv.FireTvConstants.EntityId
            };
        }
        else
        {
            _logger.LogInformation("Updating configuration for device ID '{DeviceId}'", deviceId);
            configuration.Entities.Remove(entity);
            entity = entity with
            {
                IpAddress = ipAddress,
                Port = port
            };
        }
        
        configuration.Entities.Add(entity);
        
        await _configurationService.UpdateConfigurationAsync(configuration, cancellationToken);

        return entity;
    }
    
    private async Task RemoveConfiguration(
        RemoveInstruction removeInstruction,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);

        var entities = configuration.Entities.Where(x => string.Equals(x.DeviceId, removeInstruction.DeviceId, StringComparison.Ordinal)
                                                         || removeInstruction.EntityIds?.Contains(x.EntityId, StringComparer.OrdinalIgnoreCase) is true
                                                         || x.IpAddress.Equals(removeInstruction.IpAddress, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        
        foreach (var entity in entities)
        {
            _logger.LogInformation("Removing entity {@Entity}", entity);
            configuration.Entities.Remove(entity);
        }
        
        await _configurationService.UpdateConfigurationAsync(configuration, cancellationToken);
    }
    
    private DeviceState GetDeviceState(FireTvClientHolder? fireTvClientHolder)
    {
        if (fireTvClientHolder is null)
            return DeviceState.Disconnected;
        
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                if (fireTvClientHolder.Client.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online)
                    return DeviceState.Connected;
            }

            return DeviceState.Disconnected;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to get device state");
            return DeviceState.Error;
        }
    }
    
    private record struct RemoveInstruction(string? DeviceId, string[]? EntityIds, string? IpAddress);
    private sealed record FireTvClientHolder(DeviceClient Client, in FireTvClientKey ClientKey);
}