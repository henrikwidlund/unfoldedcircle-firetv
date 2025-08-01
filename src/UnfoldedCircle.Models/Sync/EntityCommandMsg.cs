namespace UnfoldedCircle.Models.Sync;

public record MediaPlayerEntityCommandMsgData<TCommandId> : CommonReq<EntityCommandMsgData<TCommandId, MediaPlayerEntityCommandParams>>
    where TCommandId : struct, Enum;

public record RemoteEntityCommandMsgData : CommonReq<EntityCommandMsgData<string, RemoteEntityCommandParams>>;