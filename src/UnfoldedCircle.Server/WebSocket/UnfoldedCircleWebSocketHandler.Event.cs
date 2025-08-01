using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Server.Event;
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
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.ConnectEvent)!;
                var fireTvClientHolder = await TryGetFireTvClientHolder(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);

                var deviceState = GetDeviceState(fireTvClientHolder);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateConnectEventResponsePayload(deviceState,
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.Disconnect:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.DisconnectEvent)!;
                await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                var success = await TryDisconnectAdbClient(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                SocketIdEntityIpMap.TryRemove(wsId, out _);
                
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateConnectEventResponsePayload(success ? DeviceState.Disconnected : DeviceState.Error,
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.AbortDriverSetup:
            {
                _ = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.AbortDriverSetupEvent)!;
                await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                if (SocketIdEntityIpMap.TryRemove(wsId, out var ipAddress))
                {
                    await RemoveConfiguration(new RemoveInstruction(null, null, ipAddress), cancellationTokenWrapper.ApplicationStopping);
                    _logger.LogInformation("[{WSId}] WS: Removed configuration for {IpAddress}", wsId, ipAddress);
                }
                
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(0, _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
                
                return;
            }
            case MessageEvent.EnterStandby:
                {
                    _ = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.EnterStandbyEvent)!;
                    await (cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource()?.CancelAsync() ?? Task.CompletedTask);
                    _fireTvClientFactory.RemoveAllClients();
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateConnectEventResponsePayload(DeviceState.Disconnected, _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    return;
                }
            case MessageEvent.ExitStandby:
                {
                    _ = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.ExitStandbyEvent)!;
                    cancellationTokenWrapper.EnsureNonCancelledBroadcastCancellationTokenSource();
                    var fireTvClientHolder = await TryGetFireTvClientHolder(wsId, null, cancellationTokenWrapper.ApplicationStopping);
                    var deviceState = GetDeviceState(fireTvClientHolder);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateConnectEventResponsePayload(deviceState,
                            _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);

                    return;
                }
            default:
                return;
        }
    }
}