namespace UnfoldedCircle.Server.FireTv;

internal static class FireTvConstants
{
    internal const string DriverName = "Fire TV";
    internal const string DriverDescription = "Integration for Fire TVs";
    internal const string DriverId = "firetv-unfolded-circle";
    internal const string DriverVersion = "0.0.2";
    internal const string DriverDeveloper = "Henrik Widlund";
    internal const string DriverEmail = "07online_rodeo@icloud.com";
    internal static readonly DateOnly DriverReleaseDate = new(2025, 08, 02);
    internal static readonly Uri DriverUrl = new("https://github.com/henrikwidlund/unfoldedcircle-firetv");
    internal const string DeviceName = DriverName;

    internal const string IpAddressKey = "ip_address";
    internal const string MacAddressKey = "mac_address";
    internal const string PortKey = "port";
    internal const string DeviceIdKey = "device_id";
}
