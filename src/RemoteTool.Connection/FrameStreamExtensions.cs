using RemoteTool.Protocol;

namespace RemoteTool.Connection;

internal static class FrameStreamExtensions
{
    /// <summary>
    /// 讀下一個封包並解成 <typeparamref name="T"/>。對方關閉拋 <see cref="EndOfStreamException"/>；
    /// 對方送 Error 拋 <see cref="RemoteErrorException"/>；類型不符拋 <see cref="ProtocolException"/>。
    /// </summary>
    public static async Task<(Frame Frame, T Message)> ReadMessageAsync<T>(this FrameStream stream, CancellationToken ct)
        where T : IMessage
    {
        Frame frame = await stream.ReadAsync(ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("對方已關閉連線");
        if (frame.Type == MessageType.Error && T.Type != MessageType.Error)
        {
            var err = MessageCodec.Decode<ErrorMessage>(frame);
            throw new RemoteErrorException(err.Code, err.Message);
        }
        if (frame.Type != T.Type)
            throw new ProtocolException(ErrorCode.ProtocolViolation, $"預期 {T.Type}，收到 {frame.Type}");
        return (frame, MessageCodec.Decode<T>(frame));
    }
}
