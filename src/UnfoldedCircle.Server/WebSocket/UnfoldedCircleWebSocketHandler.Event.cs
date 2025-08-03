using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Server.Event;
using UnfoldedCircle.Server.Json;
using UnfoldedCircle.Server.Response;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private async Task HandleEventMessage(
        System.Net.WebSockets.WebSocket socket,
        string wsId,
        MessageEvent messageEvent,
        JsonDocument jsonDocument,
        CancellationTokenWrapper cancellationTokenWrapper)
    {
        switch (messageEvent)
        {
            case MessageEvent.Connect:
            {
                cancellationTokenWrapper.EnsureNonCancelledBroadcastCancellationTokenSource();
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.ConnectEvent)!;
                var adbTvClientHolder = await TryGetAdbTvClientHolderByDeviceId(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);

                var deviceState = GetDeviceState(adbTvClientHolder);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateConnectEventResponsePayload(deviceState),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.Disconnect:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.DisconnectEvent)!;
                await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                var success = await TryDisconnectAdbClient(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                SocketIdEntityMacMap.TryRemove(wsId, out _);
                
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateConnectEventResponsePayload(success ? DeviceState.Disconnected : DeviceState.Error),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.AbortDriverSetup:
            {
                _ = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.AbortDriverSetupEvent)!;
                await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                if (SocketIdEntityMacMap.TryRemove(wsId, out var macAddress))
                {
                    await RemoveConfiguration(new RemoveInstruction(null, null, macAddress), cancellationTokenWrapper.ApplicationStopping);
                    _logger.LogInformation("[{WSId}] WS: Removed configuration for {IpAddress}", wsId, macAddress);
                }
                
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(0),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.EnterStandby:
                {
                    _ = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.EnterStandbyEvent)!;
                    await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                    _adbTvClientFactory.RemoveAllClients();
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateConnectEventResponsePayload(DeviceState.Disconnected),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    return;
                }
            case MessageEvent.ExitStandby:
                {
                    _ = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.ExitStandbyEvent)!;
                    cancellationTokenWrapper.EnsureNonCancelledBroadcastCancellationTokenSource();
                    var adbTvClientHolder = await TryGetAdbTvClientHolderByDeviceId(wsId, null, cancellationTokenWrapper.ApplicationStopping);
                    var deviceState = GetDeviceState(adbTvClientHolder);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateConnectEventResponsePayload(deviceState),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);

                    return;
                }
            default:
                return;
        }
    }
}