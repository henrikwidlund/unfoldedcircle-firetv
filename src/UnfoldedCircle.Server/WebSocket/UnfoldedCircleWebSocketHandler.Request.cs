using System.Collections.Concurrent;

using UnfoldedCircle.FireTV;
using UnfoldedCircle.Models;
using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.Server.Event;
using UnfoldedCircle.Server.FireTv;
using UnfoldedCircle.Server.Response;

namespace UnfoldedCircle.Server.WebSocket;

internal sealed partial class UnfoldedCircleWebSocketHandler
{
    private static readonly ConcurrentDictionary<string, string> SocketIdEntityIpMap = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<FireTvClientKey, RemoteState> RemoteStates = new();

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
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.CommonReq)!;
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateDriverVersionResponsePayload(
                        payload,
                        new DriverVersion
                        {
                            Name = FireTv.FireTvConstants.DriverName,
                            Version = new DriverVersionInner
                            {
                                Driver = FireTv.FireTvConstants.DriverVersion
                            }
                        }, _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetDriverMetaData:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.CommonReq)!;

                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateDriverMetaDataResponsePayload(payload, CreateDriverMetadata(), _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetDeviceState:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.GetDeviceStateMsg)!;
                var fireTvClientHolder = await TryGetFireTvClientHolder(wsId, payload.MsgData.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetDeviceStateResponsePayload(
                        GetDeviceState(fireTvClientHolder),
                        payload.MsgData.DeviceId ?? fireTvClientHolder?.Client.Device.Serial,
                        _unfoldedCircleJsonSerializerContext
                    ),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetAvailableEntities:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.GetAvailableEntitiesMsg)!;
                var entities = await GetEntities(wsId, payload.MsgData.Filter?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetAvailableEntitiesMsg(payload,
                        new AvailableEntitiesMsgData<RemoteFeature, RemoteOptions>
                        {
                            Filter = payload.MsgData.Filter,
                            AvailableEntities = GetAvailableEntities(entities)
                        },
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.SubscribeEvents:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.CommonReq)!;
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload, _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.UnsubscribeEvents:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.UnsubscribeEventsMsg)!;

                await RemoveConfiguration(new RemoveInstruction(payload.MsgData?.DeviceId, payload.MsgData?.EntityIds, null), cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateCommonResponsePayload(payload, _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.GetEntityStates:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.GetEntityStatesMsg)!;
                var entities = await GetEntities(wsId, payload.MsgData?.DeviceId, cancellationTokenWrapper.ApplicationStopping);
                await SendAsync(socket,
                    ResponsePayloadHelpers.CreateGetEntityStatesResponsePayload(payload,
                        entities is { Count: > 0 }
                            ? entities.Select(static x => new EntityIdDeviceId(x.EntityId, x.DeviceId))
                            : [],
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.SetupDriver:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.SetupDriverMsg)!;
                SocketIdEntityIpMap.AddOrUpdate(wsId,
                    static (_, arg) => arg.MsgData.SetupData[FireTv.FireTvConstants.IpAddressKey],
                    static (_, _, arg) => arg.MsgData.SetupData[FireTv.FireTvConstants.IpAddressKey], payload);

                var entity = await UpdateConfiguration(payload.MsgData.SetupData, cancellationTokenWrapper.ApplicationStopping);
                if (!await CheckClientApproved(wsId, entity.DeviceId, cancellationTokenWrapper.RequestAborted))
                {
                    await SendAsync(socket, ResponsePayloadHelpers.CreateDeviceSetupChangeUserInputResponsePayload(_unfoldedCircleJsonSerializerContext),
                        wsId, cancellationTokenWrapper.ApplicationStopping);
                    return;
                }

                await FinishSetup(socket, wsId, entity, payload, cancellationTokenWrapper);

                return;
            }
            case MessageEvent.SetupDriverUserData:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.SetDriverUserDataMsg)!;
                if (SocketIdEntityIpMap.TryGetValue(wsId, out var ipAddress))
                {
                    var entity = await _configurationService.GetConfigurationItemAsync(ipAddress, cancellationTokenWrapper.RequestAborted);
                    if (entity is null)
                    {
                        _logger.LogError("Could not find configuration item with id: {EntityId}", ipAddress);
                        await SendAsync(socket,
                            ResponsePayloadHelpers.CreateValidationErrorResponsePayload(payload,
                                new ValidationError
                                {
                                    Code = "INV_ARGUMENT",
                                    Message = "Could not find specified entity"
                                },
                                _unfoldedCircleJsonSerializerContext),
                            wsId,
                            cancellationTokenWrapper.ApplicationStopping);
                        return;
                    }
                    if (!await CheckClientApproved(wsId, entity.DeviceId, cancellationTokenWrapper.RequestAborted))
                    {
                        await SendAsync(socket, ResponsePayloadHelpers.CreateDeviceSetupChangeUserInputResponsePayload(_unfoldedCircleJsonSerializerContext),
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
                        },
                        _unfoldedCircleJsonSerializerContext),
                    wsId,
                    cancellationTokenWrapper.ApplicationStopping);

                return;
            }
            case MessageEvent.EntityCommand:
            {
                var payload = jsonDocument.Deserialize(_unfoldedCircleJsonSerializerContext.RemoteEntityCommandMsgData)!;
                await HandleEntityCommand(socket, payload, wsId, payload.MsgData.DeviceId, cancellationTokenWrapper);
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
        var fireTvClientHolder = await TryGetFireTvClientHolder(wsId, entity.DeviceId, cancellationTokenWrapper.ApplicationStopping);

        var isConnected = fireTvClientHolder is not null && fireTvClientHolder.Client.Device.State == AdvancedSharpAdbClient.Models.DeviceState.Online;
        if (fireTvClientHolder is not null)
            _logger.LogInformation("Setup of Fire TV: {FireTv}", fireTvClientHolder.Client.Device.ToString());

        await Task.WhenAll(
            SendAsync(socket,
                ResponsePayloadHelpers.CreateCommonResponsePayload(payload, _unfoldedCircleJsonSerializerContext),
                wsId,
                cancellationTokenWrapper.ApplicationStopping),
            SendAsync(socket,
                ResponsePayloadHelpers.CreateDeviceSetupChangeResponsePayload(isConnected, _unfoldedCircleJsonSerializerContext),
                wsId,
                cancellationTokenWrapper.ApplicationStopping),
            SendAsync(socket,
                ResponsePayloadHelpers.CreateConnectEventResponsePayload(GetDeviceState(fireTvClientHolder),
                    _unfoldedCircleJsonSerializerContext),
                wsId,
                cancellationTokenWrapper.ApplicationStopping)
        );
    }

    private static readonly RemoteOptions RemoteOptions = new()
    {
        ButtonMapping =
        [
            new DeviceButtonMapping { Button = RemoteButtons.Home, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.Home } },
            new DeviceButtonMapping { Button = RemoteButtons.Back, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.Back } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadDown, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.DpadDown } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadUp, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.DpadUp } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadLeft, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.DpadLeft } },
            new DeviceButtonMapping { Button = RemoteButtons.ChannelUp, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.ChannelUp } },
            new DeviceButtonMapping { Button = RemoteButtons.ChannelDown, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.ChannelDown } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadRight, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.DpadRight } },
            new DeviceButtonMapping { Button = RemoteButtons.DpadMiddle, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.DpadCenter } },
            new DeviceButtonMapping { Button = RemoteButtons.VolumeUp, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.VolumeUp } },
            new DeviceButtonMapping { Button = RemoteButtons.VolumeDown, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.VolumeDown } },
            new DeviceButtonMapping { Button = RemoteButtons.Power, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.Power } },
            new DeviceButtonMapping { Button = RemoteButtons.Mute, ShortPress = new EntityCommand { CmdId = FireTV.FireTvConstants.Mute } }
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
            RemoteCommands.InputHdmi4
        ],
        UserInterface = new UserInterface
        {
            Pages =
            [
                new UserInterfacePage
                {
                    PageId = "uc_firetv_general",
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
                    PageId = "uc_firetv_numpad",
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
                Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = $"{FireTv.FireTvConstants.DeviceName} {x.IpAddress}" },
                DeviceId = x.DeviceId,
                Features = FireTvEntitySettings.RemoteFeatures,
                Options = RemoteOptions
            }).ToArray()
            : [];

    private static DriverMetadata? _driverMetadata;
    private static DriverMetadata CreateDriverMetadata() =>
        _driverMetadata ??= new DriverMetadata
        {
            Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = FireTv.FireTvConstants.DriverName
            },
            Description = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = FireTv.FireTvConstants.DriverDescription
            },
            Version = FireTv.FireTvConstants.DriverVersion,
            DriverId = FireTv.FireTvConstants.DriverId,
            Developer = new DriverDeveloper { Email = FireTv.FireTvConstants.DriverEmail, Name = FireTv.FireTvConstants.DriverDeveloper, Url = FireTv.FireTvConstants.DriverUrl },
            ReleaseDate = FireTv.FireTvConstants.DriverReleaseDate,
            SetupDataSchema = new SettingsPage
            {
                Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = "Enter Details"
                },
                Settings =
                [
                    new Setting
                    {
                        Id = "disclaimer",
                        Field =  new SettingTypeLabel
                        {
                            Label = new SettingTypeLabelItem
                            {
                                Value = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["en"] = "Note that you must have enabled Developer Settings on your TV."
                                }
                            }
                        },
                        Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["en"] = ""
                        }
                    },
                    new Setting
                    {
                        Id = FireTv.FireTvConstants.MacAddressKey,
                        Field = new SettingTypeText
                        {
                            Text = new ValueRegex
                            {
                                RegEx = MacAddressRegex,
                                Value = string.Empty
                            }
                        },
                        Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["en"] = "Enter the MAC address of the Fire TV"
                        }
                    },
                    new Setting
                    {
                        Id = FireTv.FireTvConstants.IpAddressKey,
                        Field = new SettingTypeText
                        {
                            Text = new ValueRegex
                            {
                                RegEx = Ipv4Or6,
                                Value = string.Empty
                            }
                        },
                        Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["en"] = "Enter the IP address of the Fire TV"
                        }
                    },
                    new Setting
                    {
                        Id = FireTv.FireTvConstants.PortKey,
                        Field = new SettingTypeNumber
                            {
                                Number = new SettingTypeNumberInner
                                {
                                    Value = 5555,
                                    Min = 1,
                                    Max = 65535,
                                    Decimals = 0
                                }
                            },
                        Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["en"] = "Enter the ADB port of the Fire TV"
                        }
                    }
                ]
            },
            DeviceDiscovery = false,
            Icon = "custom:firetv.png"
        };

    private const string MacAddressRegex = "^([0-9a-fA-F]{2}[:-]){5}([0-9a-fA-F]{2})$";
    private const string Ipv4Or6 = """^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|(([0-9a-fA-F]{1,4}:){7,7}[0-9a-fA-F]{1,4}|([0-9a-fA-F]{1,4}:){1,7}:|([0-9a-fA-F]{1,4}:){1,6}:[0-9a-fA-F]{1,4}|([0-9a-fA-F]{1,4}:){1,5}(:[0-9a-fA-F]{1,4}){1,2}|([0-9a-fA-F]{1,4}:){1,4}(:[0-9a-fA-F]{1,4}){1,3}|([0-9a-fA-F]{1,4}:){1,3}(:[0-9a-fA-F]{1,4}){1,4}|([0-9a-fA-F]{1,4}:){1,2}(:[0-9a-fA-F]{1,4}){1,5}|[0-9a-fA-F]{1,4}:((:[0-9a-fA-F]{1,4}){1,6})|:((:[0-9a-fA-F]{1,4}){1,7}|:)|fe80:(:[0-9a-fA-F]{0,4}){0,4}%[0-9a-zA-Z]{1,}|::(ffff(:0{1,4}){0,1}:){0,1}((25[0-5]|(2[0-4]|1{0,1}[0-9]){0,1}[0-9])\.){3,3}(25[0-5]|(2[0-4]|1{0,1}[0-9]){0,1}[0-9])|([0-9a-fA-F]{1,4}:){1,4}:((25[0-5]|(2[0-4]|1{0,1}[0-9]){0,1}[0-9])\.){3,3}(25[0-5]|(2[0-4]|1{0,1}[0-9]){0,1}[0-9]))$""";
}