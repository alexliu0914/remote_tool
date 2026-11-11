using System.Net;
using RemoteTool.Connection;
using Xunit;

namespace RemoteTool.Connection.Tests;

public class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remotetool-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PasswordHasher_VerifiesCorrect_RejectsWrong()
    {
        var rec = PasswordHasher.Create("alex", "密碼123", iterations: 1000);
        Assert.True(PasswordHasher.Verify(rec, "alex", "密碼123"));
        Assert.False(PasswordHasher.Verify(rec, "alex", "密碼124"));
        Assert.False(PasswordHasher.Verify(rec, "Alex", "密碼123"));
        Assert.False(PasswordHasher.Verify(rec, "alex", ""));
    }

    [Fact]
    public void PasswordHasher_UsesRandomSalt()
    {
        var a = PasswordHasher.Create("u", "p", 1000);
        var b = PasswordHasher.Create("u", "p", 1000);
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void CredentialStore_RoundTrips_WithoutPlaintext()
    {
        string path = Path.Combine(_dir, "cred.json");
        CredentialStore.Save(path, PasswordHasher.Create("alex", "SuperSecret!", 1000));

        Assert.DoesNotContain("SuperSecret!", File.ReadAllText(path));
        var loaded = CredentialStore.Load(path)!;
        Assert.True(PasswordHasher.Verify(loaded, "alex", "SuperSecret!"));
    }

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

    [Fact]
    public void LoginThrottle_BlocksAfterLimit_AndExpires()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(3, TimeSpan.FromMinutes(5), () => now);
        var ip = IPAddress.Parse("10.0.0.5");

        for (int i = 0; i < 3; i++) throttle.RecordFailure(ip);
        Assert.True(throttle.IsBlocked(ip));
        Assert.False(throttle.IsBlocked(IPAddress.Parse("10.0.0.6")));

        now = now.AddMinutes(6);
        Assert.False(throttle.IsBlocked(ip));
    }

    [Fact]
    public void LoginThrottle_TreatsMappedIPv4AsSame()
    {
        var throttle = new LoginThrottle(1);
        throttle.RecordFailure(IPAddress.Parse("10.0.0.5"));
        Assert.True(throttle.IsBlocked(IPAddress.Parse("10.0.0.5").MapToIPv6()));
    }
}
