namespace UnfoldedCircle.AdbTv;

public readonly record struct AdbTvClientKey(string IpAddress, string MacAddress, in int Port);