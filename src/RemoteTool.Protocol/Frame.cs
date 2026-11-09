namespace RemoteTool.Protocol;

/// <summary>
/// 一個封包：通道、類型與內容。內容為已序列化的位元組（上層用 MessagePack）。
/// </summary>
public readonly record struct Frame(ChannelKind Channel, MessageType Type, ReadOnlyMemory<byte> Payload);
