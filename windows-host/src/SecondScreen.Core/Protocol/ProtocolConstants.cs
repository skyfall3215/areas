namespace SecondScreen.Core.Protocol;

public static class ProtocolConstants
{
    public const byte Magic0 = 0x53; // 'S'
    public const byte Magic1 = 0x53; // 'S'
    public const byte Version1 = 0x01;
    public const int HeaderSize = 16;
    public const int DefaultPort = 27183;
    public const int DiscoveryPort = 27184;
    public const uint MaxPayloadLength = 4 * 1024 * 1024; // 4 MB

    public const byte TransportModeTcp = 0x00;

    public const byte VideoFlagKeyframe = 0x01;
    public const byte VideoFlagCursorEmbedded = 0x02;

    public const byte FeatureFlagTouch = 0x01;
    public const byte FeatureFlagLowLatencyDecoder = 0x02;

    public const byte CodecIdH264 = 0x01;

    public const byte HelloStatusOk = 0x00;
    public const byte HelloStatusVersionMismatch = 0x01;
    public const byte HelloStatusEncoderInitFailed = 0x02;
    public const byte HelloStatusAuthFailed = 0x03;
}

public enum MessageType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    VideoConfig = 0x10,
    VideoFrame = 0x11,
    KeyframeReq = 0x12,
    TouchEvent = 0x20,
    KeyboardEvent = 0x21,
    Ping = 0x30,
    Pong = 0x31,
    Error = 0xF0,
    Disconnect = 0xFF
}

