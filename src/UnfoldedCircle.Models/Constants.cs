namespace UnfoldedCircle.Models;

public static class RemoteCommands
{
    public const string Home = "HOME";
    public const string Back = "BACK";
    public const string Digit0 = "DIGIT_0";
    public const string Digit1 = "DIGIT_1";
    public const string Digit2 = "DIGIT_2";
    public const string Digit3 = "DIGIT_3";
    public const string Digit4 = "DIGIT_4";
    public const string Digit5 = "DIGIT_5";
    public const string Digit6 = "DIGIT_6";
    public const string Digit7 = "DIGIT_7";
    public const string Digit8 = "DIGIT_8";
    public const string Digit9 = "DIGIT_9";
    public const string CursorUp = "CURSOR_UP";
    public const string CursorDown = "CURSOR_DOWN";
    public const string CursorLeft = "CURSOR_LEFT";
    public const string CursorRight = "CURSOR_RIGHT";
    public const string CursorEnter = "CURSOR_ENTER";
    public const string VolumeUp = "VOLUME_UP";
    public const string VolumeDown = "VOLUME_DOWN";
    public const string MuteToggle = "MUTE_TOGGLE";
    public const string Info = "INFO";
    public const string ChannelUp = "CHANNEL_UP";
    public const string ChannelDown = "CHANNEL_DOWN";
    public const string Settings = "SETTINGS";
    public const string InputHdmi1 = "INPUT_HDMI1";
    public const string InputHdmi2 = "INPUT_HDMI2";
    public const string InputHdmi3 = "INPUT_HDMI3";
    public const string InputHdmi4 = "INPUT_HDMI4";
}

public static class RemoteButtons
{
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Toggle = "TOGGLE";
    public const string Home = RemoteCommands.Home;
    public const string Back = RemoteCommands.Back;
    public const string DpadDown = "DPAD_DOWN";
    public const string DpadUp = "DPAD_UP";
    public const string DpadLeft = "DPAD_LEFT";
    public const string DpadRight = "DPAD_RIGHT";
    public const string DpadMiddle = "DPAD_MIDDLE";
    public const string ChannelUp = RemoteCommands.ChannelUp;
    public const string ChannelDown = RemoteCommands.ChannelDown;
    public const string VolumeUp = RemoteCommands.VolumeUp;
    public const string VolumeDown = RemoteCommands.VolumeDown;
    public const string Power = "POWER";
    public const string Mute = "MUTE";
}