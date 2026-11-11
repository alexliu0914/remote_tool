using MessagePack;

namespace RemoteTool.Protocol;

/// <summary>
/// 型別化訊息與 <see cref="Frame"/> 之間的轉換（MessagePack）。
/// 反序列化一律以「不受信任資料」模式進行，格式錯誤時拋出 <see cref="InvalidDataException"/>。
/// </summary>
public static class MessageCodec
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static Frame Encode<T>(ChannelKind channel, T message) where T : IMessage
        => new(channel, T.Type, MessagePackSerializer.Serialize(message, Options));

    /// <summary>
    /// 把封包解成指定訊息。類型不符、內容格式錯誤或內容含多餘位元組時拋出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static T Decode<T>(Frame frame) where T : IMessage
    {
        if (frame.Type != T.Type)
            throw new InvalidDataException($"預期 {T.Type}，收到 {frame.Type}");

        T? result;
        try
        {
            result = MessagePackSerializer.Deserialize<T>(frame.Payload, Options, out int bytesRead);
            if (bytesRead != frame.Payload.Length)
                throw new InvalidDataException($"{T.Type} 內容含多餘位元組");
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidDataException($"{T.Type} 內容格式錯誤", ex);
        }

        return result ?? throw new InvalidDataException($"{T.Type} 內容為空");
    }

    /// <summary>此類型是否為本版協定認得的訊息類型。</summary>
    public static bool IsKnownType(MessageType type) => Enum.IsDefined(type);
}
