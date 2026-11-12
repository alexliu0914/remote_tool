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
        Assert.True(loaded.Verify("alex", "SuperSecret!42"));
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

    // ---- 主機信任：以公鑰識別（G-06） ----

    private const string FpA = "AA:AA", FpB = "BB:BB";

    [Fact]
    public void KnownHosts_TrustIsByKey_NotAddress()
    {
        var k = new KnownHostsStore(null);
        Assert.Equal(HostTrust.Unknown, k.Evaluate("10.0.0.1:52100", FpA));
        k.Trust("10.0.0.1:52100", FpA);
        Assert.Equal(HostTrust.Trusted, k.Evaluate("10.0.0.1:52100", FpA));
        Assert.Equal(HostTrust.Trusted, k.Evaluate("10.0.0.99:52100", FpA)); // 同一台換 IP
        Assert.Equal(HostTrust.Changed, k.Evaluate("10.0.0.1:52100", FpB));  // 同位址換金鑰
        Assert.Equal(HostTrust.Unknown, k.Evaluate("10.0.0.2:52100", FpB));  // 新位址、新金鑰
    }

    [Fact]
    public void KnownHosts_Forget_RemovesKeyAndAllItsAddresses()
    {
        var k = new KnownHostsStore(null);
        k.Trust("10.0.0.1:52100", FpA);
        k.RecordAddress("10.0.0.9:52100", FpA);
        k.Trust("10.0.0.2:52100", FpB);
        Assert.True(k.Forget("10.0.0.9:52100"));
        Assert.False(k.IsTrusted(FpA));
        Assert.Null(k.HintFor("10.0.0.1:52100"));
        Assert.True(k.IsTrusted(FpB));
        Assert.False(k.Forget("10.0.0.1:52100"));
    }

    [Fact]
    public void KnownHosts_RecordAddress_IgnoresUntrustedKeys()
    {
        var k = new KnownHostsStore(null);
        k.RecordAddress("10.0.0.1:52100", FpA);
        Assert.Null(k.HintFor("10.0.0.1:52100"));
    }

    [Fact]
    public void KnownHosts_Persists_AndIsProtected()
    {
        string path = Path.Combine(_dir, "known.json");
        var protector = new TestProtector();
        new KnownHostsStore(path, protector).Trust("10.0.0.1:52100", FpA);

        Assert.StartsWith("RTSEC1:TEST\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 12));
        Assert.DoesNotContain("AA:AA", File.ReadAllText(path));
        var reloaded = new KnownHostsStore(path, protector);
        Assert.True(reloaded.IsTrusted(FpA));
        Assert.Equal(FpA, reloaded.HintFor("10.0.0.1:52100"));
    }

    [Fact]
    public void KnownHosts_LegacyFile_IsMigrated()
    {
        string path = Path.Combine(_dir, "legacy-known.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "192.168.1.20:52100": "AA:AA" }"""); // G-06 之前的明文格式
        var protector = new TestProtector();
        var k = new KnownHostsStore(path, protector);
        Assert.True(k.IsTrusted(FpA));
        Assert.Equal(FpA, k.HintFor("192.168.1.20:52100"));
        Assert.StartsWith("RTSEC1:TEST", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 11)); // 已改存為受保護格式
    }

    // ---- 秘密檔案保護（G-03） ----

    /// <summary>測試用保護方式：加上檢查碼後以 purpose 衍生的位元組做 XOR；用途不符時解不出檢查碼。</summary>
    private sealed class TestProtector : ISecretProtector
    {
        private static readonly byte[] Tag = Encoding.ASCII.GetBytes("OK:");
        public string Name => "TEST";
        public byte[] Protect(byte[] data, string purpose) => Xor(Tag.Concat(data).ToArray(), purpose);
        public byte[] Unprotect(byte[] data, string purpose)
        {
            var plain = Xor(data, purpose);
            if (!plain.AsSpan().StartsWith(Tag)) throw new System.Security.Cryptography.CryptographicException("用途不符");
            return plain[Tag.Length..];
        }
        private static byte[] Xor(byte[] data, string purpose)
        {
            byte key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(purpose))[0];
            if (key == 0) key = 0x5A;
            return data.Select(b => (byte)(b ^ key)).ToArray();
        }
    }

    [Fact]
    public void SecretFile_PlainLegacy_IsReadAndFlaggedForRewrite()
    {
        string path = Path.Combine(_dir, "a.bin");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var data = SecretFile.ReadBytes(path, new PlainProtector(), out bool rewrite);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        Assert.True(rewrite);
    }

    [Fact]
    public void SecretFile_FileCopiedToAnotherPurpose_FailsToDecrypt()
    {
        var protector = new DpapiLikeProtector();
        string a = Path.Combine(_dir, "host-cert.pfx"), b = Path.Combine(_dir, "host-credentials.json");
        SecretFile.WriteBytes(a, Encoding.UTF8.GetBytes("secret"), protector);
        Directory.CreateDirectory(_dir);
        File.Copy(a, b, overwrite: true);
        Assert.Equal("secret", Encoding.UTF8.GetString(SecretFile.ReadBytes(a, protector, out bool r1)));
        Assert.False(r1);
        Assert.Throws<InvalidDataException>(() => SecretFile.ReadBytes(b, protector, out _));
    }

    /// <summary>以 purpose 衍生金鑰的 AES-GCM，模擬 DPAPI 的「用途綁定」行為（跨平台）。</summary>
    private sealed class DpapiLikeProtector : ISecretProtector
    {
        private static readonly byte[] Master = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public string Name => "TESTGCM";
        public byte[] Protect(byte[] data, string purpose)
        {
            using var aes = new System.Security.Cryptography.AesGcm(Key(purpose), 16);
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[data.Length];
            aes.Encrypt(nonce, data, cipher, tag);
            return nonce.Concat(tag).Concat(cipher).ToArray();
        }
        public byte[] Unprotect(byte[] data, string purpose)
        {
            using var aes = new System.Security.Cryptography.AesGcm(Key(purpose), 16);
            var plain = new byte[data.Length - 28];
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return plain;
        }
        private static byte[] Key(string purpose)
            => System.Security.Cryptography.HMACSHA256.HashData(Master, Encoding.UTF8.GetBytes(purpose));
    }

    [Fact]
    public void Dpapi_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI 只在 Windows（CI 為 windows-latest，會實際執行）
        string path = Path.Combine(_dir, "dpapi.bin");
        SecretFile.WriteBytes(path, Encoding.UTF8.GetBytes("秘密"), new DpapiProtector());
        Assert.StartsWith("RTSEC1:DPAPI\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 13));
        Assert.Equal("秘密", Encoding.UTF8.GetString(SecretFile.ReadBytes(path, new DpapiProtector(), out _)));
    }

    [Fact]
    public void Credentials_AreProtected_AndLegacyPlainFileIsMigrated()
    {
        string path = Path.Combine(_dir, "cred.json");
        Directory.CreateDirectory(_dir);
        var rec = PasswordHasher.Create("alex", "password-1234"); // 正式參數（Load 會拒絕弱參數）
        // G-04 之前的單一帳號、明文格式
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(rec));
        var protector = new DpapiLikeProtector();
        var set = CredentialStore.Load(path, protector)!;
        Assert.True(set.Verify("alex", "password-1234"));
        Assert.StartsWith("RTSEC1:TESTGCM", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 14));
        Assert.Equal(1, CredentialStore.Load(path, protector)!.Count);
    }

    [Fact]
    public void HostCertificate_LegacyPlainPfx_IsMigrated_KeepingFingerprint()
    {
        string path = Path.Combine(_dir, "host.pfx");
        Directory.CreateDirectory(_dir);
        string original;
        using (var c = HostCertificate.LoadOrCreate(path, new PlainProtector()))
            original = Fingerprint.Compute(c);
        // 模擬舊版：去掉檔頭，只留 PFX
        var raw = File.ReadAllBytes(path);
        int nl = Array.IndexOf(raw, (byte)'\n');
        File.WriteAllBytes(path, raw[(nl + 1)..]);

        var protector = new DpapiLikeProtector();
        using var migrated = HostCertificate.LoadOrCreate(path, protector);
        Assert.Equal(original, Fingerprint.Compute(migrated));
        Assert.StartsWith("RTSEC1:TESTGCM", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 14));
        using var again = HostCertificate.LoadOrCreate(path, protector);
        Assert.Equal(original, Fingerprint.Compute(again));
    }

    // ---- 多組帳號（G-04） ----

    [Fact]
    public void CredentialSet_MultipleAccounts_EachVerifiesOwnPassword()
    {
        var set = new CredentialSet([
            PasswordHasher.Create("alex", "alex-password-1", Weak, true),
            PasswordHasher.Create("bob", "bob-password-22", Weak, true)]);
        Assert.True(set.Verify("alex", "alex-password-1", true));
        Assert.True(set.Verify("bob", "bob-password-22", true));
        Assert.False(set.Verify("alex", "bob-password-22", true));
        Assert.False(set.Verify("carol", "alex-password-1", true)); // 不存在的帳號
        Assert.False(new CredentialSet([]).Verify("alex", "x", true));
    }

    [Fact]
    public void CredentialSet_AddReplaceRemove_AndRejectsDuplicates()
    {
        var set = new CredentialSet([PasswordHasher.Create("alex", "old-password-1", Weak, true)]);
        set = set.With(PasswordHasher.Create("alex", "new-password-1", Weak, true)).With(PasswordHasher.Create("bob", "bob-password-22", Weak, true));
        Assert.Equal(2, set.Count);
        Assert.False(set.Verify("alex", "old-password-1", true));
        Assert.True(set.Verify("alex", "new-password-1", true));
        set = set.Without("alex");
        Assert.False(set.Contains("alex"));
        var r = PasswordHasher.Create("x", "password-1234", Weak, true);
        Assert.Throws<InvalidDataException>(() => new CredentialSet([r, r]));
    }

    [Fact]
    public void CredentialStore_MultipleAccounts_RoundTrip()
    {
        string path = Path.Combine(_dir, "multi.json");
        var set = new CredentialSet([
            PasswordHasher.Create("alex", "alex-password-1"),
            PasswordHasher.Create("bob", "bob-password-22")]);
        CredentialStore.Save(path, set, new TestProtector());
        var loaded = CredentialStore.Load(path, new TestProtector())!;
        Assert.Equal(new[] { "alex", "bob" }, loaded.Accounts.Select(a => a.Username));
        Assert.True(loaded.Verify("bob", "bob-password-22"));
    }

    [Fact]
    public void HostCertificate_IsStableAcrossLoads()
    {
        string path = Path.Combine(_dir, "host.pfx");
        using var a = HostCertificate.LoadOrCreate(path, new PlainProtector());
        using var b = HostCertificate.LoadOrCreate(path, new PlainProtector());
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
