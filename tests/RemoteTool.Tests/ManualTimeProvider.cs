namespace RemoteTool.Tests;

/// <summary>可手動推進的時間來源（單元測試用；只支援時間戳與 UTC 時間，不支援計時器）。</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _utc = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => _utc;

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _ticks, by.Ticks);
        _utc += by;
    }
}
