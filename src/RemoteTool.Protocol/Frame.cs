namespace RemoteTool.Protocol;

/// <summary>
/// 一個封包。表頭 <c>[Length][MessageType][RequestId][Sequence]</c>（第七節），
/// 內容為 MessagePack 序列化後的位元組。
/// </summary>
/// <param name="Type">訊息類型。</param>
/// <param name="RequestId">請求與回覆配對用；不需配對時為 0。</param>
/// <param name="Sequence">每個方向從 1 起逐一遞增，由 <see cref="FramedConnection"/> 填寫與檢查。</param>
/// <param name="Payload">訊息內容。</param>
public readonly record struct Frame(MessageType Type, uint RequestId, uint Sequence, ReadOnlyMemory<byte> Payload);
