using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.FireTv;
using UnfoldedCircle.Server.Json;

namespace UnfoldedCircle.Server.Response;

internal static class ResponsePayloadHelpers
{
    const string EventKind = "event";
    
    private static byte[]? _createAuthResponsePayload;
    internal static byte[] CreateAuthResponsePayload(UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        _createAuthResponsePayload ??= JsonSerializer.SerializeToUtf8Bytes(new AuthMsg
            {
                Kind = "resp",
                ReqId = 0,
                Msg = "authentication",
                Code = 200
            },
            jsonSerializerContext.AuthMsg);

    internal static byte[] CreateDriverVersionResponsePayload(
        CommonReq req,
        DriverVersion driverVersionResponseData,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverVersionMsg
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "get_driver_version",
                Code = 200,
                MsgData = driverVersionResponseData
            },
            jsonSerializerContext.DriverVersionMsg);

    internal static byte[] CreateDriverMetaDataResponsePayload(
        CommonReq req,
        DriverMetadata driverMetadata,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverMetadataMsg
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "driver_metadata",
                Code = 200,
                MsgData = driverMetadata
            },
            jsonSerializerContext.DriverMetadataMsg);

    private const string Device = "DEVICE";

    internal static byte[] CreateGetDeviceStateResponsePayload(
        in DeviceState deviceState,
        string? deviceId,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new DeviceStateEventMsg
        {
            Kind = EventKind,
            Msg = "device_state",
            Cat = Device,
            TimeStamp = DateTime.UtcNow,
            MsgData = new DeviceStateItem
            {
                State = deviceState,
                DeviceId = deviceId
            }
        }, jsonSerializerContext.DeviceStateEventMsg);

    internal static byte[] CreateGetAvailableEntitiesMsg<TFeature, TOptions>(
        GetAvailableEntitiesMsg req,
        AvailableEntitiesMsgData<TFeature, TOptions> availableEntitiesMsgData,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext)
        where TFeature : struct, Enum =>
        JsonSerializer.SerializeToUtf8Bytes(new AvailableEntitiesMsg<TFeature, TOptions>
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "available_entities",
                Code = 200,
                MsgData = availableEntitiesMsgData
            },
            jsonSerializerContext.AvailableEntitiesMsgRemoteFeatureRemoteOptions);

    public static byte[] CreateCommonResponsePayload(
        CommonReq req,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        CreateCommonResponsePayload(req.Id, jsonSerializerContext);

    public static byte[] CreateCommonResponsePayload(
        in uint requestId,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new CommonResp
            {
                Code = 200,
                Kind = "resp",
                ReqId = requestId,
                Msg = "result"
            },
            jsonSerializerContext.CommonResp);

    public static byte[] CreateGetEntityStatesResponsePayload(
        CommonReq req,
        in bool isConnected,
        string? deviceId,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new EntityStates<RemoteEntityAttribute>
        {
            Code = 200,
            Kind = "resp",
            ReqId = req.Id,
            Msg = "entity_states",
            MsgData = isConnected
                ?
                [
                    new EntityStateChanged<RemoteEntityAttribute>
                    {
                        EntityId = FireTvConstants.EntityId,
                        EntityType = EntityType.Remote,
                        Attributes = RemoteEntityAttributes,
                        DeviceId = deviceId
                    }
                ]
                : []
        }, jsonSerializerContext.EntityStatesRemoteEntityAttribute);

    private static readonly RemoteEntityAttribute[] RemoteEntityAttributes =
    [
        RemoteEntityAttribute.State
    ];
    
    public static byte[] CreateDeviceSetupChangeResponsePayload(
        in bool isConnected,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverSetupChangeEvent
        {
            Kind = EventKind,
            Msg = "driver_setup_change",
            Cat = Device,
            TimeStamp = DateTime.UtcNow,
            MsgData = new DriverSetupChange
            {
                State = isConnected ? DriverSetupChangeState.Ok : DriverSetupChangeState.Error,
                EventType = DriverSetupChangeEventType.Stop,
                Error = isConnected ? null : DriverSetupChangeError.NotFound
            }
        }, jsonSerializerContext.DriverSetupChangeEvent);

    public static byte[] CreateDeviceSetupChangeUserInputResponsePayload(
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverSetupChangeEvent
        {
            Kind = EventKind,
            Msg = "driver_setup_change",
            Cat = Device,
            TimeStamp = DateTime.UtcNow,
            MsgData = new DriverSetupChange
            {
                State = DriverSetupChangeState.WaitUserAction,
                EventType = DriverSetupChangeEventType.Setup,
                RequireUserAction = new RequireUserAction
                {
                    Confirmation = new ConfirmationPage
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["en"] = "Confirm ADB Access on Fire TV"
                        }
                    }
                }
            }
        }, jsonSerializerContext.DriverSetupChangeEvent);

    public static byte[] CreateConnectEventResponsePayload(
        in DeviceState deviceState,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new ConnectEventMsg
        {
            Kind = EventKind,
            Msg = "device_state",
            Cat = Device,
            TimeStamp = DateTime.UtcNow,
            MsgData = new ConnectDeviceStateItem { State = deviceState }
        }, jsonSerializerContext.ConnectEventMsg);
    }

    internal static byte[] CreateValidationErrorResponsePayload(
        CommonReq req,
        ValidationError validationError,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext) =>
        JsonSerializer.SerializeToUtf8Bytes(new CommonRespRequired<ValidationError>
        {
            Kind = "resp",
            ReqId = req.Id,
            Msg = "validation_error",
            Code = 400,
            MsgData = validationError
        }, jsonSerializerContext.CommonRespRequiredValidationError);

    internal static byte[] CreateStateChangedResponsePayload(
        RemoteStateChangedEventMessageDataAttributes attributes,
        UnfoldedCircleJsonSerializerContext jsonSerializerContext)
        =>
        JsonSerializer.SerializeToUtf8Bytes(new StateChangedEvent<RemoteStateChangedEventMessageDataAttributes>
        {
            Kind = EventKind,
            Msg = "entity_change",
            Cat = "ENTITY",
            TimeStamp = DateTime.UtcNow,
            MsgData = new StateChangedEventMessageData<RemoteStateChangedEventMessageDataAttributes>
            {
                EntityId = FireTvConstants.EntityId,
                EntityType = EntityType.Remote,
                Attributes = attributes
            }
        }, jsonSerializerContext.StateChangedEventRemoteStateChangedEventMessageDataAttributes);
}