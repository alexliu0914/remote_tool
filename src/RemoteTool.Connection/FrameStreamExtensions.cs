using RemoteTool.Protocol;

namespace RemoteTool.Connection;

internal static class FrameStreamExtensions
{
    /// <summary>讀下一個封包並解成 <typeparamref name="T"/>；通道不符、對方關閉或送 Error 時拋例外。</summary>
    public static async Task<T> ReadMessageAsync<T>(this FrameStream stream, ChannelKind channel, CancellationToken ct)
        where T : IMessage
    {
        Frame frame = await stream.ReadAsync(ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("對方已關閉連線");
        if (frame.Channel != channel)
            throw new ProtocolViolationException($"通道不符：預期 {channel}，收到 {frame.Channel}");
        if (frame.Type == MessageType.Error && T.Type != MessageType.Error)
            throw new ProtocolViolationException("對方回報錯誤：" + MessageCodec.Decode<ErrorMessage>(frame).Message);
        return MessageCodec.Decode<T>(frame);
    }
}
