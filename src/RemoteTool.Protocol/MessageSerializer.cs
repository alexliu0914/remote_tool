using System.Collections.Frozen;
using MessagePack;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Protocol;

/// <summary>
/// 訊息型別與 <see cref="MessageType"/> 的固定對應，以及安全的 MessagePack 序列化。
/// 反序列化使用 <see cref="MessagePackSecurity.UntrustedData"/>，並檢查封包類型與預期型別一致。
/// </summary>
public static class MessageSerializer
{
    private static readonly FrozenDictionary<Type, MessageType> TypeMap = new Dictionary<Type, MessageType>
    {
        [typeof(Hello)] = MessageType.Hello,
        [typeof(HelloAck)] = MessageType.HelloAck,
        [typeof(LoginRequest)] = MessageType.LoginRequest,
        [typeof(LoginResult)] = MessageType.LoginResult,
        [typeof(SessionTokenRequest)] = MessageType.SessionTokenRequest,
        [typeof(SessionTokenGrant)] = MessageType.SessionTokenGrant,
        [typeof(SubConnectAuth)] = MessageType.SubConnectAuth,
        [typeof(SubConnectResult)] = MessageType.SubConnectResult,
        [typeof(Heartbeat)] = MessageType.Heartbeat,
        [typeof(HeartbeatAck)] = MessageType.HeartbeatAck,
        [typeof(Disconnect)] = MessageType.Disconnect,
        [typeof(PauseControl)] = MessageType.PauseControl,
        [typeof(ResumeControl)] = MessageType.ResumeControl,
        [typeof(ResumeAck)] = MessageType.ResumeAck,
        [typeof(KeyEvent)] = MessageType.KeyEvent,
        [typeof(MouseMoveEvent)] = MessageType.MouseMoveEvent,
        [typeof(MouseButtonEvent)] = MessageType.MouseButtonEvent,
        [typeof(MouseWheelEvent)] = MessageType.MouseWheelEvent,
        [typeof(UnicodeTextEvent)] = MessageType.UnicodeTextEvent,
        [typeof(InputHeartbeat)] = MessageType.InputHeartbeat,
        [typeof(ReleaseAllInput)] = MessageType.ReleaseAllInput,
        [typeof(VideoFrame)] = MessageType.VideoFrame,
        [typeof(KeyframeRequest)] = MessageType.KeyframeRequest,
        [typeof(ErrorMessage)] = MessageType.Error,
    }.ToFrozenDictionary();

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>取得訊息型別對應的封包類型。</summary>
    public static MessageType TypeOf<T>() => TypeMap.TryGetValue(typeof(T), out var t)
        ? t
        : throw new InvalidOperationException($"{typeof(T).Name} 沒有對應的 MessageType");

    /// <summary>序列化（送出前也檢查，避免送出自己都會拒絕的資料）。</summary>
    public static byte[] Serialize<T>(T message) where T : notnull
    {
        if (message is IMessage m)
            m.Validate();
        return MessagePackSerializer.Serialize(message, Options);
    }

    /// <summary>反序列化並檢查。任何錯誤都轉為 <see cref="InvalidDataException"/>。</summary>
    public static T Deserialize<T>(ReadOnlyMemory<byte> payload) where T : IMessage
    {
        T value;
        try
        {
            value = MessagePackSerializer.Deserialize<T>(payload, Options, out int bytesRead);
            if (bytesRead != payload.Length)
                throw new InvalidDataException($"{typeof(T).Name} 後面有多餘資料");
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidDataException($"{typeof(T).Name} 格式錯誤", ex);
        }
        if (value is null)
            throw new InvalidDataException($"{typeof(T).Name} 不可為 null");
        value.Validate();
        return value;
    }

    /// <summary>確認封包類型後反序列化。</summary>
    public static T Read<T>(in Frame frame) where T : IMessage
    {
        var expected = TypeOf<T>();
        if (frame.Type != expected)
            throw new InvalidDataException($"預期 {expected}，收到 {frame.Type}");
        return Deserialize<T>(frame.Payload);
    }
}
