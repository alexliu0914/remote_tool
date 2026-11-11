using MessagePack;
using MessagePack.Resolvers;

namespace RemoteTool.Protocol;

/// <summary>
/// 型別化訊息與 MessagePack 之間的轉換（Protocol V1）。
/// 安全設定：<see cref="MessagePackSecurity.UntrustedData"/>（防雜湊碰撞、限制物件深度 16），
/// 只反序列化成固定的 sealed record 型別，訊息中沒有 object / Typeless 欄位，不接受任意型別。
/// 格式錯誤、類型不符或含多餘位元組一律拋出 <see cref="InvalidDataException"/>。
/// </summary>
public static class MessageCodec
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(16));

    public static byte[] Serialize<T>(T message) where T : IMessage
        => MessagePackSerializer.Serialize(message, Options);

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
}
