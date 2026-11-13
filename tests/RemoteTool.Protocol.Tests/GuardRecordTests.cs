using RemoteTool.Protocol;
using Xunit;

namespace RemoteTool.Protocol.Tests;

public class GuardRecordTests
{
    private static GuardRecord RoundTrip(GuardRecord r)
    {
        var buf = new byte[GuardRecord.Size];
        r.WriteTo(buf);
        Assert.True(GuardRecord.TryRead(buf, out var back));
        return back;
    }

    [Fact]
    public void AllTypes_RoundTrip()
    {
        Assert.Equal(GuardRecord.Key(1, 0x1E, false, true), RoundTrip(GuardRecord.Key(1, 0x1E, false, true)));
        Assert.Equal(GuardRecord.Key(2, 0x4B, true, false), RoundTrip(GuardRecord.Key(2, 0x4B, true, false)));
        Assert.Equal(GuardRecord.Move(1, 65535, 0), RoundTrip(GuardRecord.Move(1, 65535, 0)));
        Assert.Equal(GuardRecord.Button(1, MouseButton.Right, true, 10, 20), RoundTrip(GuardRecord.Button(1, MouseButton.Right, true, 10, 20)));
        var wheel = RoundTrip(GuardRecord.Wheel(1, -240, true, 5, 6));
        Assert.Equal((short)-240, unchecked((short)wheel.Value16));
        Assert.True(wheel.Horizontal);
        Assert.Equal(GuardRecordType.ReleaseAll, RoundTrip(GuardRecord.ReleaseAll(3)).Type);
    }

    [Fact]
    public void Layout_IsLittleEndian16Bytes()
    {
        var buf = new byte[16];
        GuardRecord.Key(0x01020304, 0x1E, true, true).WriteTo(buf);
        Assert.Equal(new byte[] { 1, 3, 0x1E, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 3, 2, 1 }, buf);
    }

    [Theory]
    [InlineData(new byte[] { 99, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]          // 白名單外類型
    [InlineData(new byte[] { 1, 0x80, 0x1E, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]     // 未定義旗標
    [InlineData(new byte[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]           // 掃描碼 0
    [InlineData(new byte[] { 1, 1, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]           // 掃描碼 0x200
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]           // X = 65536
    [InlineData(new byte[] { 3, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]           // 未定義滑鼠按鍵
    public void InvalidRecords_AreRejected(byte[] raw) => Assert.False(GuardRecord.TryRead(raw, out _));

    [Fact]
    public void ShortBuffer_IsRejected() => Assert.False(GuardRecord.TryRead(new byte[15], out _));
}
