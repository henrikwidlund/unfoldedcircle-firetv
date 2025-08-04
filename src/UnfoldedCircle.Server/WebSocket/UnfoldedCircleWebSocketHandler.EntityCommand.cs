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
        string entityId,
        CancellationTokenWrapper cancellationTokenWrapper)
    {
        var adbTvClientHolder = await TryGetAdbTvClientHolder(wsId, entityId, IdentifierType.EntityId, cancellationTokenWrapper.ApplicationStopping);
        if (adbTvClientHolder is null || adbTvClientHolder.Client.Device.State != DeviceState.Online)
        {
            await SendAsync(socket,
                ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                    new ValidationError
                    {
                        Code = "INV_ARGUMENT",
                        Message = adbTvClientHolder is null ? "Device not found" : "Device not connected"
                    }),
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
                            payload.MsgData.EntityId),
                        wsId,
                        cancellationTokenWrapper.ApplicationStopping);
                    break;
                case "off":
                    await adbTvClientHolder.Client.SendKeyEventAsync(AdbTvConstants.Sleep, cancellationTokenWrapper.ApplicationStopping);
                    await SendAsync(socket,
                        ResponsePayloadHelpers.CreateStateChangedResponsePayload(
                            new RemoteStateChangedEventMessageDataAttributes { State = RemoteStates[adbTvClientHolder.ClientKey] = RemoteState.Off },
                            payload.MsgData.EntityId),
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
                    success = await HandleSendCommand(payload, cancellationTokenWrapper, adbTvClientHolder, _logger);
                    break;
                case "send_cmd_sequence":
                    success = await HandleSendCommandSequence(payload, cancellationTokenWrapper, adbTvClientHolder, _logger);
                    break;
                default:
                    success = false;
                    break;
            }

            if (success)
            {
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload),
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
                        }),
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
                    }),
                wsId,
                cancellationTokenWrapper.ApplicationStopping);
        }
    }

    private static async Task<bool> HandleSendCommand(RemoteEntityCommandMsgData payload,
        CancellationTokenWrapper cancellationTokenWrapper,
        AdbTvClientHolder adbTvClientHolder,
        ILogger logger)
    {
        (string command, bool isRawCommand) = GetMappedCommand(payload.MsgData.Params?.Command);
        if (string.IsNullOrEmpty(command))
            return false;

        var delay = payload.MsgData.Params?.Delay ?? 0;
        if (payload.MsgData.Params?.Repeat.HasValue is true)
        {
            for (var i = 0; i < payload.MsgData.Params.Repeat.Value; i++)
            {
                logger.LogTrace("Sending command '{Command}' to device {IpAddress} (repeat {Repeat})",
                    command, adbTvClientHolder.ClientKey.IpAddress, i + 1);

                await (isRawCommand ?
                    adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping) :
                    adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));
                if (delay> 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
            }
        }
        else
        {
            logger.LogTrace("Sending command '{Command}' to device {IpAddress}",
                command, adbTvClientHolder.ClientKey.IpAddress);
            await (isRawCommand
                ? adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping)
                : adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));
        }

        return true;
    }

    private static async Task<bool> HandleSendCommandSequence(RemoteEntityCommandMsgData payload,
        CancellationTokenWrapper cancellationTokenWrapper,
        AdbTvClientHolder adbTvClientHolder,
        ILogger logger)
    {
        if (payload.MsgData.Params is not { Sequence: { Length: > 0 } sequence })
            return false;

        var delay = payload.MsgData.Params?.Delay ?? 0;
        var shouldRepeat = payload.MsgData.Params?.Repeat.HasValue is true;
        foreach ((string command, bool isRawCommand) in sequence.Select(GetMappedCommand).Where(static x => !string.IsNullOrEmpty(x.Command)))
        {
            if (shouldRepeat)
            {
                for (var i = 0; i < payload.MsgData.Params!.Repeat!.Value; i++)
                {
                    logger.LogTrace("Sending command '{Command}' as part of a sequence to device {MacAddress} (repeat {Repeat})",
                        command, adbTvClientHolder.ClientKey.MacAddress, i + 1);
                    await (isRawCommand ?
                        adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping) :
                        adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));
                    if (delay> 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
                }
            }
            else
            {
                logger.LogTrace("Sending command '{Command}' as part of a sequence to device {IpAddress}",
                    command, adbTvClientHolder.ClientKey.IpAddress);
                await (isRawCommand ?
                    adbTvClientHolder.Client.AdbClient.ExecuteRemoteCommandAsync(command, adbTvClientHolder.Client.Device, cancellationTokenWrapper.ApplicationStopping) :
                    adbTvClientHolder.Client.SendKeyEventAsync(command, cancellationTokenWrapper.ApplicationStopping));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationTokenWrapper.ApplicationStopping);
            }
        }

        return true;
    }

    private static (string Command, bool IsRawCommand) GetMappedCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return (string.Empty, false);

        return command switch
        {
            _ when command.Equals(RemoteButtons.On, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Wakeup, false),
            _ when command.Equals(RemoteButtons.Off, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Sleep, false),
            _ when command.Equals(RemoteButtons.Toggle, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Power, false),
            _ when command.Equals(RemoteButtons.VolumeUp, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.VolumeUp, false),
            _ when command.Equals(RemoteButtons.VolumeDown, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.VolumeDown, false),
            _ when command.Equals(RemoteCommands.MuteToggle, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Mute, false),
            _ when command.Equals(RemoteButtons.Mute, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Mute, false),
            _ when command.Equals(RemoteButtons.ChannelUp, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.ChannelUp, false),
            _ when command.Equals(RemoteButtons.ChannelDown, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.ChannelDown, false),
            _ when command.Equals(RemoteCommands.CursorUp, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadUp, false),
            _ when command.Equals(RemoteButtons.DpadUp, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadUp, false),
            _ when command.Equals(RemoteCommands.CursorDown, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadDown, false),
            _ when command.Equals(RemoteButtons.DpadDown, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadDown, false),
            _ when command.Equals(RemoteCommands.CursorLeft, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadLeft, false),
            _ when command.Equals(RemoteButtons.DpadLeft, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadLeft, false),
            _ when command.Equals(RemoteCommands.CursorRight, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadRight, false),
            _ when command.Equals(RemoteButtons.DpadRight, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadRight, false),
            _ when command.Equals(RemoteCommands.CursorEnter, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadCenter, false),
            _ when command.Equals(RemoteButtons.DpadMiddle, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.DpadCenter, false),
            _ when command.Equals(RemoteCommands.Digit0, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key0, false),
            _ when command.Equals(RemoteCommands.Digit1, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key1, false),
            _ when command.Equals(RemoteCommands.Digit2, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key2, false),
            _ when command.Equals(RemoteCommands.Digit3, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key3, false),
            _ when command.Equals(RemoteCommands.Digit4, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key4, false),
            _ when command.Equals(RemoteCommands.Digit5, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key5, false),
            _ when command.Equals(RemoteCommands.Digit6, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key6, false),
            _ when command.Equals(RemoteCommands.Digit7, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key7, false),
            _ when command.Equals(RemoteCommands.Digit8, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key8, false),
            _ when command.Equals(RemoteCommands.Digit9, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Key9, false),
            _ when command.Equals(RemoteButtons.Home, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Home, false),
            _ when command.Equals(RemoteCommands.Info, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Info, false),
            _ when command.Equals(RemoteButtons.Back, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Back, false),
            _ when command.Equals(RemoteCommands.Settings, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Settings, false),
            _ when command.Equals(RemoteCommands.InputHdmi1, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Hdmi1, false),
            _ when command.Equals(RemoteCommands.InputHdmi2, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Hdmi2, false),
            _ when command.Equals(RemoteCommands.InputHdmi3, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Hdmi3, false),
            _ when command.Equals(RemoteCommands.InputHdmi4, StringComparison.OrdinalIgnoreCase) => (AdbTvConstants.Hdmi4, false),
            _ => GetRawCommand(command)
        };

        static (string Command, bool IsRawCommand) GetRawCommand(string command)
        {
            return command switch
            {
                _ when command.StartsWith("RAW:", StringComparison.OrdinalIgnoreCase) => (command[4..], true),
                _ when command.StartsWith("APP:", StringComparison.OrdinalIgnoreCase) => ($"monkey --pct-syskeys 0 -p {command[4..]} 1", true),
                _ when command.StartsWith("ACT:", StringComparison.OrdinalIgnoreCase) => ($"am start -n {command[4..]}", true),
                _ when command.StartsWith("INP:", StringComparison.OrdinalIgnoreCase) => (
                    $"am start -a android.intent.action.VIEW -d content://android.media.tv/passthrough/com.mediatek.tvinput%2F.hdmi.HDMIInputService%2FHW{command[4..]} -n org.droidtv.playtv/.PlayTvActivity -f 0x10000000",
                    true),
                _ => (command, false)
            };
        }
    }
}