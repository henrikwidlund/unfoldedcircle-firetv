namespace UnfoldedCircle.FireTV;

public readonly record struct FireTvClientKey(string IpAddress, string MacAddress, in int Port);