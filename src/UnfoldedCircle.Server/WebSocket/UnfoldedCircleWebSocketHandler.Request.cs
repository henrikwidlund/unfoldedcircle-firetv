using System.Collections.Concurrent;

using UnfoldedCircle.AdbTv;
using UnfoldedCircle.Models;
using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.AdbTv;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.Server.Event;
using UnfoldedCircle.Server.Json;
using UnfoldedCircle.Server.Response;

using AdbTvConstants = UnfoldedCircle.AdbTv.AdbTvConstants;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private static readonly ConcurrentDictionary<string, string> SocketIdEntityMacMap = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<AdbTvClientKey, RemoteState> RemoteStates = new();

    private async Task HandleRequestMessage(
        System.Net.WebSockets.WebSocket socket,
        string wsId,
        MessageEvent messageEvent,
        JsonDocument jsonDocument,
        CancellationTokenWrapper cancellationTokenWrapper)
    {
        switch (messageEvent)
        {
            case MessageEvent.GetDriverVersion:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.CommonReq)!;
                var driverMetadata = await _configurationService.GetDriverMetadataAsync(cancellationTokenWrapper.RequestAborted);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateDriverVersionResponsePayload(
                        payload,
                        new DriverVersion
                        {
                            Name = driverMetadata.Name["en"],
                            Version = new DriverVersionInner
                            {
                                Driver = driverMetadata.Version
                            }
                        }),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetDriverMetaData:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.CommonReq)!;

                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateDriverMetaDataResponsePayload(payload, await _configurationService.GetDriverMetadataAsync(cancellationTokenWrapper.RequestAborted)),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetDeviceState:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.GetDeviceStateMsg)!;
                var adbTvClientHolder = await TryGetAdbTvClientHolder(wsId, payload.MsgData.DeviceId, IdentifierType.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetDeviceStateResponsePayload(
                        GetDeviceState(adbTvClientHolder),
                        payload.MsgData.DeviceId
                    ),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetAvailableEntities:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.GetAvailableEntitiesMsg)!;
                var entities = await GetEntities(wsId, payload.MsgData.Filter?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetAvailableEntitiesMsg(payload,
                        new AvailableEntitiesMsgData<RemoteFeature, RemoteOptions>
                        {
                            Filter = payload.MsgData.Filter,
                            AvailableEntities = GetAvailableEntities(entities)
                        }),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.SubscribeEvents:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.CommonReq)!;
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.UnsubscribeEvents:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.UnsubscribeEventsMsg)!;

                await RemoveConfiguration(new RemoveInstruction(payload.MsgData?.DeviceId, payload.MsgData?.EntityIds, null), cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetEntityStates:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.GetEntityStatesMsg)!;
                var entities = await GetEntities(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetEntityStatesResponsePayload(payload,
                        entities is { Count: > 0 }
                            ? entities.Select(static x => new EntityIdDeviceId(x.EntityId, x.DeviceId))
                            : []),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.SetupDriver:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.SetupDriverMsg)!;
                SocketIdEntityMacMap.AddOrUpdate(wsId,
                    static (_, arg) => arg.MsgData.SetupData[AdbTvServerConstants.MacAddressKey],
                    static (_, _, arg) => arg.MsgData.SetupData[AdbTvServerConstants.MacAddressKey], payload);

                var entity = await UpdateConfiguration(payload.MsgData.SetupData, cancellationTokenWrapper.ApplicationStopping);
                if (!await CheckClientApproved(wsId, entity.EntityId, cancellationTokenWrapper.RequestAborted))
                {
                    await SendAsync(socket, ResponsePayloadHelpers.CreateDeviceSetupChangeUserInputResponsePayload(),
                        wsId, cancellationTokenWrapper.ApplicationStopping);
                    return;
                }

                await FinishSetup(socket, wsId, entity, payload, cancellationTokenWrapper);

                return;
            }
            case MessageEvent.SetupDriverUserData:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.SetDriverUserDataMsg)!;
                if (SocketIdEntityMacMap.TryGetValue(wsId, out var macAddress))
                {
                    var entity = await _configurationService.GetConfigurationItemAsync(macAddress, cancellationTokenWrapper.RequestAborted);
                    if (entity is null)
                    {
                        _logger.LogError("Could not find configuration item with id: {EntityId}", macAddress);
                        await SendAsync(socket,
                            ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                                new ValidationError
                                {
                                    Code = "INV_ARGUMENT",
                                    Message = "Could not find specified entity"
                                }),
                            wsId,
                            cancellationTokenWrapper.ApplicationStopping);
                        return;
                    }
                    if (!await CheckClientApproved(wsId, entity.EntityId, cancellationTokenWrapper.RequestAborted))
                    {
                        await SendAsync(socket, ResponsePayloadHelpers.CreateDeviceSetupChangeUserInputResponsePayload(),
                            wsId, cancellationTokenWrapper.ApplicationStopping);
                        return;
                    }

                    await FinishSetup(socket, wsId, entity, payload, cancellationTokenWrapper);
                    return;
                }

                _logger.LogError("Could not find entity for WSId: {EntityId}", wsId);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                        new ValidationError
                        {
                            Code = "INV_ARGUMENT",
                            Message = "Could not find specified entity"
                        }),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.EntityCommand:
            {
                var payload = jsonDocument.Deserialize(UnfoldedCircleJsonSerializerContext.Instance.RemoteEntityCommandMsgData)!;
                await HandleEntityCommand(socket, payload, wsId, payload.MsgData.EntityId, cancellationTokenWrapper);
                return;
            }
            case MessageEvent.SupportedEntityTypes:
            default:
                return;
        }
    }

    private async Task FinishSetup(System.Net.WebSockets.WebSocket socket,
        string wsId,
        UnfoldedCircleConfigurationItem entity,
        CommonReq payload,
        CancellationTokenWrapper cancellationTokenWrapper)
    {
        var adbTvClientHolder = await TryGetAdbTvClientHolder(wsId, entity.EntityId, IdentifierType.EntityId, cancellationTokenWrapper.ApplicationStopping);

        var isConnected = adbTvClientHolder is not null && adbTvClientHolder.Client.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online;
        if (adbTvClientHolder is not null)
            _logger.LogInformation("Setup of ADB TV: {ADBTv}", adbTvClientHolder.Client.Device.ToString());

        await Task.WhenAll(
            SendAsync(socket,
                ResponsePayloadHelpers.CreateCommonResponsePayload(payload),
                wsId,
                cancellationTokenWrapper.ApplicationStopping),
            SendAsync(socket,
                ResponsePayloadHelpers.CreateDeviceSetupChangeResponsePayload(isConnected),
                wsId,
                cancellationTokenWrapper.ApplicationStopping),
            SendAsync(socket,
                ResponsePayloadHelpers.CreateConnectEventResponsePayload(GetDeviceState(adbTvClientHolder)),
                wsId,
                cancellationTokenWrapper.ApplicationStopping)
        );
    }

    private static readonly RemoteOptions RemoteOptions = new()
    {
        ButtonMapping =
        [
            new DeviceButtonMapping { Button = RemoteButtons.Home, ShortPress = new EntityCommand { CmdId = AdbTvConstants.Home } },
            new DeviceButtonMapping { Button = RemoteButtons.Back, ShortPress = new EntityCommand { CmdId = AdbTvConstants.Back } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadDown, ShortPress = new EntityCommand { CmdId = AdbTvConstants.DpadDown } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadUp, ShortPress = new EntityCommand { CmdId = AdbTvConstants.DpadUp } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadLeft, ShortPress = new EntityCommand { CmdId = AdbTvConstants.DpadLeft } },
            new DeviceButtonMapping { Button = RemoteButtons.ChannelUp, ShortPress = new EntityCommand { CmdId = AdbTvConstants.ChannelUp } },
            new DeviceButtonMapping { Button = RemoteButtons.ChannelDown, ShortPress = new EntityCommand { CmdId = AdbTvConstants.ChannelDown } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadRight, ShortPress = new EntityCommand { CmdId = AdbTvConstants.DpadRight } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadMiddle, ShortPress = new EntityCommand { CmdId = AdbTvConstants.DpadCenter } },
            new DeviceButtonMapping { Button = RemoteButtons.VolumeUp, ShortPress = new EntityCommand { CmdId = AdbTvConstants.VolumeUp } },
            new DeviceButtonMapping { Button = RemoteButtons.VolumeDown, ShortPress = new EntityCommand { CmdId = AdbTvConstants.VolumeDown } },
            new DeviceButtonMapping { Button = RemoteButtons.Power, ShortPress = new EntityCommand { CmdId = AdbTvConstants.Power } },
            new DeviceButtonMapping { Button = RemoteButtons.Mute, ShortPress = new EntityCommand { CmdId = AdbTvConstants.Mute } }
        ],
        SimpleCommands =
        [
            RemoteCommands.Home, RemoteCommands.Back, RemoteCommands.Digit0,
            RemoteCommands.Digit1, RemoteCommands.Digit2, RemoteCommands.Digit3,
            RemoteCommands.Digit4, RemoteCommands.Digit5, RemoteCommands.Digit6,
            RemoteCommands.Digit7, RemoteCommands.Digit8, RemoteCommands.Digit9,
            RemoteCommands.CursorUp, RemoteCommands.CursorDown, RemoteCommands.CursorLeft,
            RemoteCommands.CursorRight, RemoteCommands.CursorEnter, RemoteCommands.VolumeUp,
            RemoteCommands.VolumeDown, RemoteCommands.MuteToggle, RemoteCommands.Info,
            RemoteCommands.ChannelUp, RemoteCommands.ChannelDown, RemoteCommands.Settings,
            RemoteCommands.InputHdmi1, RemoteCommands.InputHdmi2, RemoteCommands.InputHdmi3,
            RemoteCommands.InputHdmi4, ..AppNames.SupportedApps
        ],
        UserInterface = new UserInterface
        {
            Pages =
            [
                new UserInterfacePage
                {
                    PageId = "uc_adbtv_general",
                    Name = "General",
                    Grid = new Grid { Height = 4, Width = 2 },
                    Items =
                    [
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "HDMI 1",
                            Command = new EntityCommand { CmdId = RemoteCommands.InputHdmi1 },
                            Location = new GridLocation { X = 0, Y = 0 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "HDMI 2",
                            Command = new EntityCommand { CmdId = RemoteCommands.InputHdmi2 },
                            Location = new GridLocation { X = 1, Y = 0 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "HDMI 3",
                            Command = new EntityCommand { CmdId = RemoteCommands.InputHdmi3 },
                            Location = new GridLocation { X = 0, Y = 1 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "HDMI 4",
                            Command = new EntityCommand { CmdId = RemoteCommands.InputHdmi4 },
                            Location = new GridLocation { X = 1, Y = 1 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "Info",
                            Command = new EntityCommand { CmdId = RemoteCommands.Info },
                            Location = new GridLocation { X = 0, Y = 3 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "Settings",
                            Command = new EntityCommand { CmdId = RemoteCommands.Settings },
                            Location = new GridLocation { X = 1, Y = 3 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        }
                    ]
                },
                new UserInterfacePage
                {
                    PageId = "uc_adbtv_numpad",
                    Name = "Numpad",
                    Grid = new Grid { Height = 4, Width = 3 },
                    Items =
                    [
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "1",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit1 },
                            Location = new GridLocation { X = 0, Y = 0 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "2",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit2 },
                            Location = new GridLocation { X = 1, Y = 0 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "3",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit3 },
                            Location = new GridLocation { X = 2, Y = 0 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },

                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "4",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit4 },
                            Location = new GridLocation { X = 0, Y = 1 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "5",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit5 },
                            Location = new GridLocation { X = 1, Y = 1 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "6",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit6 },
                            Location = new GridLocation { X = 2, Y = 1 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },

                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "7",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit7 },
                            Location = new GridLocation { X = 0, Y = 2 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "8",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit8 },
                            Location = new GridLocation { X = 1, Y = 2 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "9",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit9 },
                            Location = new GridLocation { X = 2, Y = 2 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                        new UserInterfaceItem
                        {
                            Type = UserInterfaceItemType.Text,
                            Text = "0",
                            Command = new EntityCommand { CmdId = RemoteCommands.Digit0 },
                            Location = new GridLocation { X = 1, Y = 3 },
                            Size = new GridItemSize { Height = 1, Width = 1 }
                        },
                    ]
                }
            ]
        }
    };

    private static AvailableEntity<RemoteFeature, RemoteOptions>[] GetAvailableEntities(
        List<UnfoldedCircleConfigurationItem>? entities) =>
        entities is { Count: > 0 }
            ? entities.Select(static x => new AvailableEntity<RemoteFeature, RemoteOptions>
            {
                EntityId = x.EntityId,
                EntityType = EntityType.Remote,
                Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = x.DeviceName },
                DeviceId = x.DeviceId,
                Features = AdbTvEntitySettings.RemoteFeatures,
                Options = RemoteOptions
            }).ToArray()
            : [];
}