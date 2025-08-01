using System.Net;

using AdvancedSharpAdbClient.Models;

using UnfoldedCircle.Models;
using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.Response;
using UnfoldedCircle.Server.WoL;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private async Task HandleEntityCommand(
        System.Net.WebSockets.WebSocket socket,
        RemoteEntityCommandMsgData payload,
        string wsId,
        string? deviceId,
        CancellationTokenWrapper cancellationTokenWrapper)
    {
        var fireTvClientHolder = await TryGetFireTvClientHolder(wsId, deviceId, cancellationTokenWrapper.ApplicationStopping);
        if (fireTvClientHolder is null || fireTvClientHolder.Client.Device.State != DeviceState.Online)
        {
            await SendAsync(socket,
                ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                    new ValidationError
                    {
                        Code = "INV_ARGUMENT",
                        Message = fireTvClientHolder is null ? "Device not found" : "Device not connected"
                    },
                    _unfoldedCircleJsonSerializerContext),
                wsId,
                cancellationTokenWrapper.ApplicationStopping);
            return;
        }

        try
        {
            var success = true;
            switch (payload.MsgData.CommandId)
            {
                case "on":
                    try
                    {
                        await WakeOnLan.SendWakeOnLanAsync(IPAddress.Parse(fireTvClientHolder.ClientKey.IpAddress), fireTvClientHolder.ClientKey.MacAddress);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "[{WSId}] WS: Error while sending Wake-on-LAN to {IpAddress} ({MacAddress})",
                            wsId,
                            fireTvClientHolder.ClientKey.IpAddress,
                            fireTvClientHolder.ClientKey.MacAddress);
                    }

                    await fireTvClientHolder.Client.SendKeyEventAsync(FireTV.FireTvConstants.Wakeup, cancellationTokenWrapper.ApplicationStopping);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateStateChangedResponsePayload(
                            new RemoteStateChangedEventMessageDataAttributes { State = RemoteStates[fireTvClientHolder.ClientKey] = RemoteState.On },
                            _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    break;
                case "off":
                    await fireTvClientHolder.Client.SendKeyEventAsync(FireTV.FireTvConstants.Sleep, cancellationTokenWrapper.ApplicationStopping);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateStateChangedResponsePayload(
                            new RemoteStateChangedEventMessageDataAttributes { State = RemoteStates[fireTvClientHolder.ClientKey] = RemoteState.Off },
                            _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    break;
                case "toggle":
                    if (RemoteStates.TryGetValue(fireTvClientHolder.ClientKey, out var remoteState))
                        remoteState = remoteState switch
                        {
                            RemoteState.On => RemoteState.Off,
                            RemoteState.Off => RemoteState.On,
                            _ => RemoteState.Unknown
                        };
                    else
                        remoteState = RemoteState.Unknown;

                    await fireTvClientHolder.Client.SendKeyEventAsync(FireTV.FireTvConstants.Power, cancellationTokenWrapper.ApplicationStopping);
                    RemoteStates[fireTvClientHolder.ClientKey] = remoteState;
                    break;
                case "send_cmd":
                    success = await HandleSendCommand(payload, cancellationTokenWrapper, fireTvClientHolder);
                    break;
                case "send_cmd_sequence":
                    success = await HandleSendCommandSequence(payload, cancellationTokenWrapper, fireTvClientHolder);
                    break;
                default:
                    success = false;
                    break;
            }

            if (success)
            {
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload, _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
            }
            else
            {
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                        new ValidationError
                        {
                            Code = "INV_ARGUMENT",
                            Message = "Unknown command"
                        },
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{WSId}] WS: Error while handling entity command {EntityCommand}", wsId, payload.MsgData);
            await SendAsync(socket,
                ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                    new ValidationError
                    {
                        Code = "ERROR",
                        Message = "Error while handling command"
                    },
                    _unfoldedCircleJsonSerializerContext),
                wsId,
                cancellationTokenWrapper.ApplicationStopping);
        }
    }

    private static async Task<bool> HandleSendCommand(RemoteEntityCommandMsgData payload, CancellationTokenWrapper cancellationTokenWrapper, FireTvClientHolder fireTvClientHolder)
    {
        var command = GetMappedCommand(payload.MsgData.Params?.Command);
        if (string.IsNullOrEmpty(command))
            return false;

        var delay = payload.MsgData.Params?.Delay ?? 0;
        if (payload.MsgData.Params?.Repeat.HasValue is true)
        {
            for (var i = 0; i < payload.MsgData.Params.Repeat.Value; i++)
            {
                await fireTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);
                if (delay> 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
            }
        }
        else
            await fireTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);

        return true;
    }

    private static async Task<bool> HandleSendCommandSequence(RemoteEntityCommandMsgData payload, CancellationTokenWrapper cancellationTokenWrapper, FireTvClientHolder fireTvClientHolder)
    {
        if (payload.MsgData.Params is not { Sequence: { Length: > 0 } sequence })
            return false;

        var delay = payload.MsgData.Params?.Delay ?? 0;
        var shouldRepeat = payload.MsgData.Params?.Repeat.HasValue is true;
        foreach (string command in sequence.Select(GetMappedCommand).Where(static x => !string.IsNullOrEmpty(x)))
        {
            if (shouldRepeat)
            {
                for (var i = 0; i < payload.MsgData.Params!.Repeat!.Value; i++)
                {
                    await fireTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);
                    if (delay> 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
                }
            }
            else
            {
                await fireTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
            }
        }

        return true;
    }

    private static string GetMappedCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return string.Empty;

        return command switch
        {
            _ when command.Equals(RemoteButtons.On, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Wakeup,
            _ when command.Equals(RemoteButtons.Off, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Sleep,
            _ when command.Equals(RemoteButtons.Toggle, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Power,
            _ when command.Equals(RemoteButtons.VolumeUp, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.VolumeUp,
            _ when command.Equals(RemoteButtons.VolumeDown, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.VolumeDown,
            _ when command.Equals(RemoteCommands.MuteToggle, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Mute,
            _ when command.Equals(RemoteButtons.Mute, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Mute,
            _ when command.Equals(RemoteButtons.ChannelUp, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.ChannelUp,
            _ when command.Equals(RemoteButtons.ChannelDown, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.ChannelDown,
            _ when command.Equals(RemoteCommands.CursorUp, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadUp,
            _ when command.Equals(RemoteButtons.DpadUp, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadUp,
            _ when command.Equals(RemoteCommands.CursorDown, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadDown,
            _ when command.Equals(RemoteButtons.DpadDown, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadDown,
            _ when command.Equals(RemoteCommands.CursorLeft, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadLeft,
            _ when command.Equals(RemoteButtons.DpadLeft, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadLeft,
            _ when command.Equals(RemoteCommands.CursorRight, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadRight,
            _ when command.Equals(RemoteButtons.DpadRight, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadRight,
            _ when command.Equals(RemoteCommands.CursorEnter, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadCenter,
            _ when command.Equals(RemoteButtons.DpadMiddle, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.DpadCenter,
            _ when command.Equals(RemoteCommands.Digit0, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key0,
            _ when command.Equals(RemoteCommands.Digit1, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key1,
            _ when command.Equals(RemoteCommands.Digit2, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key2,
            _ when command.Equals(RemoteCommands.Digit3, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key3,
            _ when command.Equals(RemoteCommands.Digit4, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key4,
            _ when command.Equals(RemoteCommands.Digit5, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key5,
            _ when command.Equals(RemoteCommands.Digit6, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key6,
            _ when command.Equals(RemoteCommands.Digit7, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key7,
            _ when command.Equals(RemoteCommands.Digit8, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key8,
            _ when command.Equals(RemoteCommands.Digit9, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Key9,
            _ when command.Equals(RemoteButtons.Home, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Home,
            _ when command.Equals(RemoteCommands.Info, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Info,
            _ when command.Equals(RemoteButtons.Back, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Back,
            _ when command.Equals(RemoteCommands.Settings, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Settings,
            _ when command.Equals(RemoteCommands.InputHdmi1, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Hdmi1,
            _ when command.Equals(RemoteCommands.InputHdmi2, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Hdmi2,
            _ when command.Equals(RemoteCommands.InputHdmi3, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Hdmi3,
            _ when command.Equals(RemoteCommands.InputHdmi4, StringComparison.OrdinalIgnoreCase) => FireTV.FireTvConstants.Hdmi4,
            _ => command
        };
    }
}