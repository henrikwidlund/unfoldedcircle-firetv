namespace UnfoldedCircle.Models.Sync;

/// <summary>
/// Available entities response.
/// </summary>
public record AvailableEntitiesMsg<TFeature, TOptions> : CommonRespRequired<AvailableEntitiesMsgData<TFeature, TOptions>>
    where TFeature : struct, Enum;