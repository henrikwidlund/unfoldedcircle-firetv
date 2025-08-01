using System.Collections.Frozen;

using UnfoldedCircle.Models.Events;

namespace UnfoldedCircle.Server.FireTv;

public static class FireTvEntitySettings
{
    public static readonly FrozenSet<RemoteFeature> RemoteFeatures = new[]
    {
        RemoteFeature.OnOff,
        RemoteFeature.SendCmd,
        RemoteFeature.Toggle
    }.ToFrozenSet();
}