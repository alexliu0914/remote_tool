using System.Net;
using System.Security.Cryptography;
using RemoteTool.Protocol;

namespace RemoteTool.Host.Core;

/// <summary>Session Token 綁定的對象（第七節）。</summary>
public sealed record TokenBinding(string Account, int WindowsSessionId, long ControlConnectionId, ChannelRole Role);

/// <summary>
/// Session Token（第七節）：CSPRNG 256 位元、30 秒內有效、角色綁定、一次性、只存在記憶體；
/// Control 斷線、本機中斷、重新登入時全部失效。
/// 子連線以 HMAC-SHA256(Token, nonce ‖ 角色) 證明持有，不直接送出 Token。
/// </summary>
public sealed class SessionTokenService
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly IHostLog _log;
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    private sealed record Entry(byte[] Token, TokenBinding Binding, DateTimeOffset ExpiresAt);

    public SessionTokenService(TimeProvider? time = null, TimeSpan? lifetime = null, IHostLog? log = null)
    {
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime ?? ProtocolConstants.SessionTokenLifetime;
        _log = log ?? NullHostLog.Instance;
    }

    public TimeSpan Lifetime => _lifetime;

    /// <summary>目前尚未使用、未過期的 Token 數（測試用）。</summary>
    public int OutstandingCount
    {
        get { lock (_lock) { Purge(_time.GetUtcNow()); return _entries.Count; } }
    }

    public byte[] Issue(TokenBinding binding)
    {
        if (binding.Role == ChannelRole.Control)
            throw new ArgumentException("不可為 Control 發 Token", nameof(binding));

        var token = RandomNumberGenerator.GetBytes(ProtocolConstants.SessionTokenSize);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Purge(now);
            // 同一 Control 連線、同一角色只保留最新一枚
            _entries.RemoveAll(e => e.Binding.ControlConnectionId == binding.ControlConnectionId && e.Binding.Role == binding.Role);
            _entries.Add(new Entry((byte[])token.Clone(), binding, now + _lifetime));
        }
        return token;
    }

    /// <summary>
    /// 驗證子連線的證明並使該 Token 失效。重複使用、過期、跨角色一律回傳 null 並記錄來源。
    /// </summary>
    public TokenBinding? Redeem(ChannelRole role, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> proof, IPAddress? source = null)
    {
        lock (_lock)
        {
            Purge(_time.GetUtcNow());
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Binding.Role != role)
                    continue;
                if (SubConnectProof.Verify(e.Token, nonce, role, proof))
                {
                    _entries.RemoveAt(i);
                    CryptographicOperations.ZeroMemory(e.Token);
                    return e.Binding;
                }
            }
        }
        _log.Warn($"子連線 Token 驗證失敗（重複使用、過期、跨角色或偽造），角色 {role}，來源 {source}");
        return null;
    }

    public void RevokeAll(long controlConnectionId)
    {
        lock (_lock)
            _entries.RemoveAll(e => e.Binding.ControlConnectionId == controlConnectionId);
    }

    public void RevokeAll()
    {
        lock (_lock)
            _entries.Clear();
    }

    private void Purge(DateTimeOffset now) => _entries.RemoveAll(e => e.ExpiresAt <= now);
}
