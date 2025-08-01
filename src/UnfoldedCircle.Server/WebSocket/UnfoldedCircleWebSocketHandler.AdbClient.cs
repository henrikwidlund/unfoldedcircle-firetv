using System.Globalization;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;

using UnfoldedCircle.AdbTv;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Server.Configuration;

using AdbTvConstants = UnfoldedCircle.Server.AdbTv.AdbTvConstants;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private async Task<AdbTvClientKey?> TryGetAdbTvClientKey(
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
            return new AdbTvClientKey(entity.IpAddress, entity.MacAddress, entity.Port);

        _logger.LogInformation("[{WSId}] WS: No configuration found for device ID '{DeviceId}'", wsId, deviceId);
        return null;
    }

    private async Task<List<UnfoldedCircleConfigurationItem>?> GetEntities(
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

        if (!string.IsNullOrEmpty(deviceId))
        {
            var entity = configuration.Entities.Find(x => string.Equals(x.DeviceId, deviceId, StringComparison.Ordinal));
            if (entity is not null)
                return [entity];

            _logger.LogInformation("[{WSId}] WS: No configuration found for device ID '{DeviceId}'", wsId, deviceId);
            return null;
        }

        return configuration.Entities;
    }

    private async Task<AdbTvClientHolder?> TryGetAdbTvClientHolder(
        string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var adbTvClientKey = await TryGetAdbTvClientKey(wsId, deviceId, cancellationToken);
        if (adbTvClientKey is null)
            return null;

        var deviceClient = await _adbTvClientFactory.TryGetOrCreateClient(adbTvClientKey.Value, cancellationToken);
        if (deviceClient is null)
            return null;

        if (deviceClient.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online)
            return new AdbTvClientHolder(deviceClient, adbTvClientKey.Value);

        _adbTvClientFactory.TryRemoveClient(adbTvClientKey.Value);
        deviceClient = await _adbTvClientFactory.TryGetOrCreateClient(adbTvClientKey.Value, cancellationToken);

        return deviceClient is null ? null : new AdbTvClientHolder(deviceClient, adbTvClientKey.Value);
    }

    private async Task<bool> CheckClientApproved(string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var adbTvClientKey = await TryGetAdbTvClientKey(wsId, deviceId, cancellationToken);
        if (adbTvClientKey is null)
            return false;

        var adbClient = new AdbClient();
        string connectResult;
        do
        {
            connectResult = await adbClient.ConnectAsync(adbTvClientKey.Value.IpAddress, adbTvClientKey.Value.Port, cancellationToken);
        } while (!connectResult.StartsWith("already connected to ", StringComparison.InvariantCultureIgnoreCase));

        var deviceData = (await adbClient.GetDevicesAsync(cancellationToken)).FirstOrDefault(x =>
            x.Serial.Equals($"{adbTvClientKey.Value.IpAddress}:{adbTvClientKey.Value.Port.ToString(NumberFormatInfo.InvariantInfo)}", StringComparison.InvariantCulture));
        return deviceData is { State: AdvancedSharpAdbClient.Models.DeviceState.Online };
    }
    
    private async Task<bool> TryDisconnectAdbClient(
        string wsId,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var adbTvClientKey = await TryGetAdbTvClientKey(wsId, deviceId, cancellationToken);
        if (adbTvClientKey is null)
            return false;
        
        _adbTvClientFactory.TryRemoveClient(adbTvClientKey.Value);
        return true;
    }
    
    private async Task<UnfoldedCircleConfigurationItem> UpdateConfiguration(
        Dictionary<string, string> msgDataSetupData,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        var ipAddress = msgDataSetupData[AdbTvConstants.IpAddressKey];
        var macAddress = msgDataSetupData[AdbTvConstants.MacAddressKey];
        var deviceId = msgDataSetupData.GetValueOrDefault(AdbTvConstants.DeviceIdKey, ipAddress);
        var port = msgDataSetupData.TryGetValue(AdbTvConstants.PortKey, out var portValue)
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
                DeviceName = $"{AdbTvConstants.DeviceName} {ipAddress}",
                EntityId = macAddress
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
    
    private DeviceState GetDeviceState(AdbTvClientHolder? adbTvClientHolder)
    {
        if (adbTvClientHolder is null)
            return DeviceState.Disconnected;
        
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                if (adbTvClientHolder.Client.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online)
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
    private sealed record AdbTvClientHolder(DeviceClient Client, in AdbTvClientKey ClientKey);
}