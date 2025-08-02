using System.Net;

using AdvancedSharpAdbClient.Models;

using UnfoldedCircle.AdbTv;
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
        var adbTvClientHolder = await TryGetAdbTvClientHolder(wsId, deviceId, cancellationTokenWrapper.ApplicationStopping);
        if (adbTvClientHolder is null || adbTvClientHolder.Client.Device.State != DeviceState.Online)
        {
            await SendAsync(socket,
                ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                    new ValidationError
                    {
                        Code = "INV_ARGUMENT",
                        Message = adbTvClientHolder is null ? "Device not found" : "Device not connected"
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
                        await WakeOnLan.SendWakeOnLanAsync(IPAddress.Parse(adbTvClientHolder.ClientKey.IpAddress), adbTvClientHolder.ClientKey.MacAddress);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "[{WSId}] WS: Error while sending Wake-on-LAN to {IpAddress} ({MacAddress})",
                            wsId,
                            adbTvClientHolder.ClientKey.IpAddress,
                            adbTvClientHolder.ClientKey.MacAddress);
                    }

                    await adbTvClientHolder.Client.SendKeyEventAsync(AdbTvConstants.Wakeup, cancellationTokenWrapper.ApplicationStopping);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateStateChangedResponsePayload(
                            new RemoteStateChangedEventMessageDataAttributes { State = RemoteStates[adbTvClientHolder.ClientKey] = RemoteState.On },
                            payload.MsgData.EntityId,
                            _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    break;
                case "off":
                    await adbTvClientHolder.Client.SendKeyEventAsync(AdbTvConstants.Sleep, cancellationTokenWrapper.ApplicationStopping);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateStateChangedResponsePayload(
                            new RemoteStateChangedEventMessageDataAttributes { State = RemoteStates[adbTvClientHolder.ClientKey] = RemoteState.Off },
                            payload.MsgData.EntityId,
                            _unfoldedCircleJsonSerializerContext),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    break;
                case "toggle":
                    if (RemoteStates.TryGetValue(adbTvClientHolder.ClientKey, out var remoteState))
                        remoteState = remoteState switch
                        {
                            RemoteState.On => RemoteState.Off,
                            RemoteState.Off => RemoteState.On,
                            _ => RemoteState.Unknown
                        };
                    else
                        remoteState = RemoteState.Unknown;

                    await adbTvClientHolder.Client.SendKeyEventAsync(AdbTvConstants.Power, cancellationTokenWrapper.ApplicationStopping);
                    RemoteStates[adbTvClientHolder.ClientKey] = remoteState;
                    break;
                case "send_cmd":
                    success = await HandleSendCommand(payload, cancellationTokenWrapper, adbTvClientHolder);
                    break;
                case "send_cmd_sequence":
                    success = await HandleSendCommandSequence(payload, cancellationTokenWrapper, adbTvClientHolder);
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

    private static async Task<bool> HandleSendCommand(RemoteEntityCommandMsgData payload,
        CancellationTokenWrapper cancellationTokenWrapper,
        AdbTvClientHolder adbTvClientHolder)
    {
        var command = GetMappedCommand(payload.MsgData.Params?.Command);
        if (string.IsNullOrEmpty(command))
            return false;

        var isRawCommand = false;
        if (command.StartsWith("RAW:", StringComparison.OrdinalIgnoreCase))
        {
            command = command[4..];
            isRawCommand = true;
        }

        var delay = payload.MsgData.Params?.Delay ?? 0;
        if (payload.MsgData.Params?.Repeat.HasValue is true)
        {
            for (var i = 0; i < payload.MsgData.Params.Repeat.Value; i++)
            {
                await (isRawCommand ?
                    adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping) :
                    adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));
                if (delay> 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
            }
        }
        else
            await (isRawCommand ?
                adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping) :
                adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));

        return true;
    }

    private static async Task<bool> HandleSendCommandSequence(RemoteEntityCommandMsgData payload, CancellationTokenWrapper cancellationTokenWrapper, AdbTvClientHolder adbTvClientHolder)
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
                    await adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);
                    if (delay> 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
                }
            }
            else
            {
                await adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping);
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
            _ when command.Equals(RemoteButtons.On, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Wakeup,
            _ when command.Equals(RemoteButtons.Off, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Sleep,
            _ when command.Equals(RemoteButtons.Toggle, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Power,
            _ when command.Equals(RemoteButtons.VolumeUp, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.VolumeUp,
            _ when command.Equals(RemoteButtons.VolumeDown, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.VolumeDown,
            _ when command.Equals(RemoteCommands.MuteToggle, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Mute,
            _ when command.Equals(RemoteButtons.Mute, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Mute,
            _ when command.Equals(RemoteButtons.ChannelUp, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.ChannelUp,
            _ when command.Equals(RemoteButtons.ChannelDown, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.ChannelDown,
            _ when command.Equals(RemoteCommands.CursorUp, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadUp,
            _ when command.Equals(RemoteButtons.DpadUp, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadUp,
            _ when command.Equals(RemoteCommands.CursorDown, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadDown,
            _ when command.Equals(RemoteButtons.DpadDown, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadDown,
            _ when command.Equals(RemoteCommands.CursorLeft, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadLeft,
            _ when command.Equals(RemoteButtons.DpadLeft, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadLeft,
            _ when command.Equals(RemoteCommands.CursorRight, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadRight,
            _ when command.Equals(RemoteButtons.DpadRight, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadRight,
            _ when command.Equals(RemoteCommands.CursorEnter, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadCenter,
            _ when command.Equals(RemoteButtons.DpadMiddle, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.DpadCenter,
            _ when command.Equals(RemoteCommands.Digit0, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key0,
            _ when command.Equals(RemoteCommands.Digit1, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key1,
            _ when command.Equals(RemoteCommands.Digit2, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key2,
            _ when command.Equals(RemoteCommands.Digit3, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key3,
            _ when command.Equals(RemoteCommands.Digit4, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key4,
            _ when command.Equals(RemoteCommands.Digit5, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key5,
            _ when command.Equals(RemoteCommands.Digit6, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key6,
            _ when command.Equals(RemoteCommands.Digit7, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key7,
            _ when command.Equals(RemoteCommands.Digit8, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key8,
            _ when command.Equals(RemoteCommands.Digit9, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Key9,
            _ when command.Equals(RemoteButtons.Home, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Home,
            _ when command.Equals(RemoteCommands.Info, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Info,
            _ when command.Equals(RemoteButtons.Back, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Back,
            _ when command.Equals(RemoteCommands.Settings, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Settings,
            _ when command.Equals(RemoteCommands.InputHdmi1, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Hdmi1,
            _ when command.Equals(RemoteCommands.InputHdmi2, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Hdmi2,
            _ when command.Equals(RemoteCommands.InputHdmi3, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Hdmi3,
            _ when command.Equals(RemoteCommands.InputHdmi4, StringComparison.OrdinalIgnoreCase) => AdbTvConstants.Hdmi4,
            _ => command
        };
    }
}