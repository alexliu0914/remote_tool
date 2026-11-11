using System.Diagnostics;
using System.Net;
using System.Text;
using Konscious.Security.Cryptography;
using RemoteTool.Connection;
using Xunit;
using Xunit.Abstractions;

namespace RemoteTool.Connection.Tests;

public class StoreTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remotetool-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Argon2Parameters Weak = TestHost.WeakArgon2;

    public StoreTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- Argon2id（G-02） ----

    [Fact]
    public void Argon2Library_MatchesRfc9106TestVector()
    {
        // RFC 9106 §5.3 Argon2id 測試向量，確認套件實作正確
        using var argon = new Argon2id(Enumerable.Repeat((byte)0x01, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)0x02, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)0x03, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray(),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659",
            Convert.ToHexString(argon.GetBytes(32)).ToLowerInvariant());
    }

    [Fact]
    public void Create_UsesArgon2id_WithApprovedDefaults()
    {
        var rec = PasswordHasher.Create("alex", "correct horse battery");
        Assert.Equal(CredentialRecord.Argon2id, rec.Algorithm);
        Assert.Equal((19456, 2, 1), (rec.MemoryKiB, rec.Iterations, rec.Parallelism));
        Assert.Equal(16, rec.Salt.Length);
        Assert.Equal(32, rec.Hash.Length);
    }

    [Fact]
    public void Verify_CorrectAccepted_WrongRejected()
    {
        var rec = PasswordHasher.Create("alex", "密碼是十個字以上的中文", Weak, allowWeakParameters: true);
        Assert.True(PasswordHasher.Verify(rec, "alex", "密碼是十個字以上的中文", allowWeakParameters: true));
        Assert.False(PasswordHasher.Verify(rec, "alex", "密碼是十個字以上的中交", allowWeakParameters: true));
        Assert.False(PasswordHasher.Verify(rec, "Alex", "密碼是十個字以上的中文", allowWeakParameters: true));
        Assert.False(PasswordHasher.Verify(rec, "alex", "", allowWeakParameters: true));
    }

    [Fact]
    public void EachPassword_GetsIndependentRandomSalt()
    {
        var a = PasswordHasher.Create("u", "same-password-123", Weak, true);
        var b = PasswordHasher.Create("u", "same-password-123", Weak, true);
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);

        // 換掉鹽就驗證不過
        var swapped = a with { Salt = b.Salt };
        Assert.False(PasswordHasher.Verify(swapped, "u", "same-password-123", true));
    }

    [Fact]
    public void ShortPassword_IsRejected()
        => Assert.Throws<ArgumentException>(() => PasswordHasher.Create("u", "123456789", Weak, true));

    [Theory]
    [InlineData(19456, 0, 1)]          // 次數 0
    [InlineData(19456, 17, 1)]         // 次數過大
    [InlineData(19456, 2, 0)]          // 平行度 0
    [InlineData(19456, 2, 17)]         // 平行度過大
    [InlineData(4096, 2, 1)]           // 記憶體低於正式下限
    [InlineData(2 * 1024 * 1024, 2, 1)] // 記憶體 2 GiB，可被用來耗盡資源
    public void AbnormalParameters_AreRejected(int memory, int iterations, int parallelism)
    {
        var good = PasswordHasher.Create("u", "password-1234");
        var bad = good with { MemoryKiB = memory, Iterations = iterations, Parallelism = parallelism };
        Assert.Throws<InvalidDataException>(() => PasswordHasher.Verify(bad, "u", "password-1234"));
    }

    [Fact]
    public void AbnormalSaltOrHash_AreRejected()
    {
        var good = PasswordHasher.Create("u", "password-1234", Weak, true);
        Assert.Throws<InvalidDataException>(() => PasswordHasher.Verify(good with { Salt = new byte[8] }, "u", "password-1234", true));
        Assert.Throws<InvalidDataException>(() => PasswordHasher.Verify(good with { Hash = new byte[16] }, "u", "password-1234", true));
        Assert.Throws<CredentialFormatException>(() => PasswordHasher.Verify(good with { Algorithm = "pbkdf2" }, "u", "password-1234", true));
    }

    [Fact]
    public void DefaultParameters_Timing_IsReasonable()
    {
        var sw = Stopwatch.StartNew();
        var rec = PasswordHasher.Create("u", "password-1234");
        long create = sw.ElapsedMilliseconds;
        sw.Restart();
        Assert.True(PasswordHasher.Verify(rec, "u", "password-1234"));
        long verify = sw.ElapsedMilliseconds;
        _output.WriteLine($"Argon2id 19456 KiB / t=2 / p=1：建立 {create} ms、驗證 {verify} ms");
        Assert.InRange(verify, 0, 3000);
    }

    [Fact]
    public void CredentialStore_RoundTrips_WithoutPlaintext()
    {
        string path = Path.Combine(_dir, "cred.json");
        CredentialStore.Save(path, PasswordHasher.Create("alex", "SuperSecret!42"));

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("SuperSecret!42", text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("SuperSecret!42")), text);
        var loaded = CredentialStore.Load(path)!;
        Assert.True(PasswordHasher.Verify(loaded, "alex", "SuperSecret!42"));
    }

    [Fact]
    public void CredentialStore_OldPbkdf2File_RequiresSetup()
    {
        string path = Path.Combine(_dir, "old.json");
        Directory.CreateDirectory(_dir);
        // M1 第一步（c4b55e0）寫出的格式
        File.WriteAllText(path, """{ "Username": "alex", "Salt": "AAAAAAAAAAAAAAAAAAAAAA==", "Hash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "Iterations": 310000 }""");
        Assert.Throws<CredentialFormatException>(() => CredentialStore.Load(path));
    }

    [Fact]
    public void CredentialStore_TamperedParameters_AreRejectedOnLoad()
    {
        string path = Path.Combine(_dir, "tampered.json");
        CredentialStore.Save(path, PasswordHasher.Create("alex", "password-1234") with { MemoryKiB = 4 * 1024 * 1024 });
        Assert.Throws<InvalidDataException>(() => CredentialStore.Load(path));
    }

    // ---- 其他儲存 ----

    [Fact]
    public void KnownHostsStore_Persists()
    {
        string path = Path.Combine(_dir, "known.json");
        var key = KnownHostsStore.KeyFor("192.168.1.20", 52100);
        new KnownHostsStore(path).Set(key, "AA:BB");

        var reloaded = new KnownHostsStore(path);
        Assert.Equal("AA:BB", reloaded.Get(key));
        Assert.True(reloaded.Remove(key));
        Assert.Null(new KnownHostsStore(path).Get(key));
    }

    [Fact]
    public void HostCertificate_IsStableAcrossLoads()
    {
        string path = Path.Combine(_dir, "host.pfx");
        using var a = HostCertificate.LoadOrCreate(path);
        using var b = HostCertificate.LoadOrCreate(path);
        Assert.True(a.HasPrivateKey);
        Assert.Equal(Fingerprint.Compute(a), Fingerprint.Compute(b));
        Assert.Equal(32 * 3 - 1, Fingerprint.Compute(a).Length);
    }

    // ---- 登入節流（G-01） ----

    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.5");

    [Fact]
    public void Throttle_DelayDoubles_1_2_4_UpTo60()
    {
        var clock = new FakeClock();
        var t = new LoginThrottle(globalBurst: 1000, globalPerSecond: 1000, clock: () => clock.Now);
        var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };

        foreach (int seconds in expected)
        {
            Assert.True(t.TryBegin(Ip, out _));
            t.RecordFailure(Ip);
            Assert.False(t.TryBegin(Ip, out var retry));
            Assert.Equal(seconds, retry.TotalSeconds, precision: 3);
            clock.Advance(seconds);
        }
    }

    [Fact]
    public void Throttle_AttemptsDuringWait_DoNotExtendIt_AndIsNotALockout()
    {
        var clock = new FakeClock();
        var t = new LoginThrottle(globalBurst: 1000, globalPerSecond: 1000, clock: () => clock.Now);
        t.RecordFailure(Ip);
        t.RecordFailure(Ip); // 下次等 2 秒
        for (int i = 0; i < 10; i++) { Assert.False(t.TryBegin(Ip, out _)); clock.Advance(0.1); }
        clock.Advance(1.0);
        Assert.True(t.TryBegin(Ip, out _)); // 2 秒後即可再試
        Assert.Equal(2, t.FailuresFor(Ip));
    }

    [Fact]
    public void Throttle_SuccessResets_AndOtherSourcesUnaffected()
    {
        var clock = new FakeClock();
        var t = new LoginThrottle(globalBurst: 1000, globalPerSecond: 1000, clock: () => clock.Now);
        for (int i = 0; i < 5; i++) t.RecordFailure(Ip);
        Assert.True(t.TryBegin(IPAddress.Parse("10.0.0.6"), out _));
        t.RecordSuccess(Ip);
        Assert.True(t.TryBegin(Ip, out _));
        Assert.Equal(0, t.FailuresFor(Ip));
    }

    [Fact]
    public void Throttle_ForgetsOldFailures()
    {
        var clock = new FakeClock();
        var t = new LoginThrottle(forgetAfter: TimeSpan.FromMinutes(15), globalBurst: 1000, globalPerSecond: 1000, clock: () => clock.Now);
        for (int i = 0; i < 6; i++) t.RecordFailure(Ip);
        clock.Advance(16 * 60);
        Assert.True(t.TryBegin(Ip, out _));
        t.RecordFailure(Ip);
        Assert.False(t.TryBegin(Ip, out var retry));
        Assert.Equal(1, retry.TotalSeconds, precision: 3); // 從頭計算
    }

    [Fact]
    public void Throttle_GlobalBucket_LimitsAllSources()
    {
        var clock = new FakeClock();
        var t = new LoginThrottle(globalBurst: 3, globalPerSecond: 1, clock: () => clock.Now);
        for (int i = 0; i < 3; i++) Assert.True(t.TryBegin(IPAddress.Parse($"10.0.1.{i}"), out _));
        Assert.False(t.TryBegin(IPAddress.Parse("10.0.1.99"), out var retry));
        Assert.True(retry > TimeSpan.Zero);
        clock.Advance(1.1);
        Assert.True(t.TryBegin(IPAddress.Parse("10.0.1.99"), out _));
    }

    [Fact]
    public void Throttle_TreatsMappedIPv4AsSame()
    {
        var t = new LoginThrottle(globalBurst: 1000, globalPerSecond: 1000);
        t.RecordFailure(Ip);
        Assert.False(t.TryBegin(Ip.MapToIPv6(), out _));
    }
}
