namespace RemoteTool.Protocol;

/// <summary>
/// Protocol V1 的一個封包。<see cref="Payload"/> 為 MessagePack 內容。
/// <see cref="RequestId"/>：0 表示不是請求 / 回應；請求由送出方配發非 0 值，回應填入相同值。
/// <see cref="Sequence"/>：每條連線、每個方向從 1 開始，每個封包加 1，由 <see cref="FrameStream"/> 填寫與檢查。
/// </summary>
public readonly record struct Frame(MessageType Type, uint RequestId, uint Sequence, ReadOnlyMemory<byte> Payload);
