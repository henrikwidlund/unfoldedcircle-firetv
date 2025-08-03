using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.Server.Json;

namespace UnfoldedCircle.Server.Response;

internal static class ResponsePayloadHelpers
{
    const string EventKind = "event";
    
    private static byte[]? _createAuthResponsePayload;
    internal static byte[] CreateAuthResponsePayload() =>
        _createAuthResponsePayload ??= JsonSerializer.SerializeToUtf8Bytes(new AuthMsg
            {
                Kind = "resp",
                ReqId = 0,
                Msg = "authentication",
                Code = 200
            },
            UnfoldedCircleJsonSerializerContext.Instance.AuthMsg);

    internal static byte[] CreateDriverVersionResponsePayload(
        CommonReq req,
        DriverVersion driverVersionResponseData) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverVersionMsg
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "get_driver_version",
                Code = 200,
                MsgData = driverVersionResponseData
            },
            UnfoldedCircleJsonSerializerContext.Instance.DriverVersionMsg);

    internal static byte[] CreateDriverMetaDataResponsePayload(
        CommonReq req,
        DriverMetadata driverMetadata) =>
        JsonSerializer.SerializeToUtf8Bytes(new DriverMetadataMsg
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "driver_metadata",
                Code = 200,
                MsgData = driverMetadata
            },
            UnfoldedCircleJsonSerializerContext.Instance.DriverMetadataMsg);

    private const string Device = "DEVICE";

    internal static byte[] CreateGetDeviceStateResponsePayload(
        in DeviceState deviceState,
        string? deviceId) =>
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
        }, UnfoldedCircleJsonSerializerContext.Instance.DeviceStateEventMsg);

    internal static byte[] CreateGetAvailableEntitiesMsg<TFeature, TOptions>(
        GetAvailableEntitiesMsg req,
        AvailableEntitiesMsgData<TFeature, TOptions> availableEntitiesMsgData)
        where TFeature : struct, Enum =>
        JsonSerializer.SerializeToUtf8Bytes(new AvailableEntitiesMsg<TFeature, TOptions>
            {
                Kind = "resp",
                ReqId = req.Id,
                Msg = "available_entities",
                Code = 200,
                MsgData = availableEntitiesMsgData
            },
            UnfoldedCircleJsonSerializerContext.Instance.AvailableEntitiesMsgRemoteFeatureRemoteOptions);

    public static byte[] CreateCommonResponsePayload(
        CommonReq req) =>
        CreateCommonResponsePayload(req.Id);

    public static byte[] CreateCommonResponsePayload(
        in uint requestId) =>
        JsonSerializer.SerializeToUtf8Bytes(new CommonResp
            {
                Code = 200,
                Kind = "resp",
                ReqId = requestId,
                Msg = "result"
            },
            UnfoldedCircleJsonSerializerContext.Instance.CommonResp);

    public static byte[] CreateGetEntityStatesResponsePayload(
        CommonReq req,
        IEnumerable<EntityIdDeviceId> entityIdDeviceIds) =>
        JsonSerializer.SerializeToUtf8Bytes(new EntityStates<RemoteEntityAttribute>
        {
            Code = 200,
            Kind = "resp",
            ReqId = req.Id,
            Msg = "entity_states",
            MsgData = entityIdDeviceIds.Select(static x => new EntityStateChanged<RemoteEntityAttribute>
            {
                EntityId = x.EntityId,
                EntityType = EntityType.Remote,
                Attributes = RemoteEntityAttributes,
                DeviceId = x.DeviceId
            }).ToArray()
        }, UnfoldedCircleJsonSerializerContext.Instance.EntityStatesRemoteEntityAttribute);

    private static readonly RemoteEntityAttribute[] RemoteEntityAttributes =
    [
        RemoteEntityAttribute.State
    ];
    
    public static byte[] CreateDeviceSetupChangeResponsePayload(
        in bool isConnected) =>
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
        }, UnfoldedCircleJsonSerializerContext.Instance.DriverSetupChangeEvent);

    public static byte[] CreateDeviceSetupChangeUserInputResponsePayload() =>
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
                            ["en"] = "Confirm ADB Access on your TV"
                        }
                    }
                }
            }
        }, UnfoldedCircleJsonSerializerContext.Instance.DriverSetupChangeEvent);

    public static byte[] CreateConnectEventResponsePayload(
        in DeviceState deviceState)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new ConnectEventMsg
        {
            Kind = EventKind,
            Msg = "device_state",
            Cat = Device,
            TimeStamp = DateTime.UtcNow,
            MsgData = new ConnectDeviceStateItem { State = deviceState }
        }, UnfoldedCircleJsonSerializerContext.Instance.ConnectEventMsg);
    }

    internal static byte[] CreateValidationErrorResponsePayload(
        CommonReq req,
        ValidationError validationError) =>
        JsonSerializer.SerializeToUtf8Bytes(new CommonRespRequired<ValidationError>
        {
            Kind = "resp",
            ReqId = req.Id,
            Msg = "validation_error",
            Code = 400,
            MsgData = validationError
        }, UnfoldedCircleJsonSerializerContext.Instance.CommonRespRequiredValidationError);

    internal static byte[] CreateStateChangedResponsePayload(
        RemoteStateChangedEventMessageDataAttributes attributes,
        string entityId)
        =>
        JsonSerializer.SerializeToUtf8Bytes(new StateChangedEvent<RemoteStateChangedEventMessageDataAttributes>
        {
            Kind = EventKind,
            Msg = "entity_change",
            Cat = "ENTITY",
            TimeStamp = DateTime.UtcNow,
            MsgData = new StateChangedEventMessageData<RemoteStateChangedEventMessageDataAttributes>
            {
                EntityId = entityId,
                EntityType = EntityType.Remote,
                Attributes = attributes
            }
        }, UnfoldedCircleJsonSerializerContext.Instance.StateChangedEventRemoteStateChangedEventMessageDataAttributes);
}