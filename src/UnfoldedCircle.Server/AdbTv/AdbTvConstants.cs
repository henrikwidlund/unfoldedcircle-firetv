namespace UnfoldedCircle.Server.AdbTv;

internal static class AdbTvConstants
{
    internal const string DriverName = "ADB TV";
    internal const string DriverDescription = "Integration for TVs with ADB";
    internal const string DriverId = "adb-unfolded-circle";
    internal const string DriverVersion = "0.0.2";
    internal const string DriverDeveloper = "Henrik Widlund";
    internal const string DriverEmail = "07online_rodeo@icloud.com";
    internal static readonly DateOnly DriverReleaseDate = new(2025, 08, 02);
    internal static readonly Uri DriverUrl = new("https://github.com/henrikwidlund/unfoldedcircle-adbtv");
    internal const string DeviceName = DriverName;

    internal const string IpAddressKey = "ip_address";
    internal const string MacAddressKey = "mac_address";
    internal const string PortKey = "port";
    internal const string DeviceIdKey = "device_id";
}
