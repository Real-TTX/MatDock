using System.Security.Cryptography;
using System.Text;
using MatDock.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MatDock.Core.Execution;

/// <summary>
/// SSH connections kept open and shared, instead of one connection per command. Every SSH login costs the host a login of its own: sshd, PAM,
/// a login session (systemd-logind, often a <c>systemd --user</c> of its own), the user's shell; with a page that reads a host every 20 seconds,
/// the dashboard, the alerts and the metrics that is several logins a minute and a real load on a small server. A connection here serves several
/// sessions at once (each command is a channel of it; sshd allows ten per connection, <see cref="SessionsPerConnection"/> stay below), is closed
/// after <see cref="IdleFor"/> without use, and is dropped when it broke. A first contact (no saved host key yet) gets a connection of its own, so the
/// key it shows is seen and can be saved.
/// </summary>
public sealed class SshConnectionPool : IDisposable
{
    public const int SessionsPerConnection = 4;
    public const int ConnectionsPerHost = 3;
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    private readonly ISshClientFactory _factory;
    private readonly Dictionary<string, List<Pooled>> _pools = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;
    private bool _disposed;

    public SshConnectionPool(ISshClientFactory factory)
    {
        _factory = factory;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>How many connections are open now (for the tests and the logs).</summary>
    public int OpenConnections
    {
        get
        {
            lock (_pools)
            {
                return _pools.Values.Sum(p => p.Count);
            }
        }
    }

    public IHostSession Create(SshConnectionSettings settings)
        => settings.HostKeyFingerprint is null ? new SshHostSession(_factory.Create(settings)) : new PooledSession(this, Key(settings), settings);

    /// <summary>The connection a pool entry stands for: the endpoint, the user, the secrets (hashed: changed credentials open a new one), the host key.</summary>
    internal static string Key(SshConnectionSettings s)
        => string.Join('|', s.Host, s.Port, s.Username, s.AuthType, s.HostKeyFingerprint,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((s.Password ?? string.Empty) + "\0" + (s.PrivateKeyPem ?? string.Empty) + "\0" + (s.PrivateKeyPassphrase ?? string.Empty)))));

    private async Task<Pooled> LeaseAsync(string key, SshConnectionSettings settings, CancellationToken ct)
    {
        Pooled? fresh = null;
        lock (_pools)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pools.TryGetValue(key, out var list))
            {
                _pools[key] = list = [];
            }

            list.RemoveAll(p => p.Broken && p.Leases == 0 && p.Dispose());
            // One that is still connecting counts too: its sessions wait for it instead of each opening a connection of its own.
            var open = list.FirstOrDefault(p => !p.Broken && (!p.Ready || p.Client.IsConnected) && p.Leases < SessionsPerConnection);
            if (open is not null)
            {
                open.Leases++;
            }
            else if (list.Count(p => !p.Broken) < ConnectionsPerHost)
            {
                fresh = new Pooled(_factory.Create(settings), pooled: true) { Leases = 1 };
                list.Add(fresh);
            }

            fresh ??= open;
        }

        if (fresh is null)
        {
            // Busy beyond the pool: a connection of its own, as before (closed with its session).
            var own = new Pooled(_factory.Create(settings), pooled: false) { Leases = 1 };
            try
            {
                await ConnectAsync(own, ct);
            }
            catch
            {
                own.Dispose();
                throw;
            }

            return own;
        }

        try
        {
            await ConnectAsync(fresh, ct);
        }
        catch
        {
            // Broken now (ConnectAsync marked it): it goes when its last session gave it back.
            Return(fresh);
            throw;
        }

        return fresh;
    }

    private static async Task ConnectAsync(Pooled pooled, CancellationToken ct)
    {
        try
        {
            await pooled.Connecting.WaitAsync(ct);
            try
            {
                if (pooled.Broken)
                {
                    throw new SshConnectionException("The shared SSH connection could not be opened.");
                }

                if (!pooled.Client.IsConnected)
                {
                    pooled.Client.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await pooled.Client.ConnectAsync(ct);
                    pooled.Ready = true;
                }
            }
            finally
            {
                pooled.Connecting.Release();
            }
        }
        catch
        {
            pooled.Broken = true;
            throw;
        }
    }

    private void Return(Pooled pooled)
    {
        if (!pooled.InPool)
        {
            pooled.Dispose();
            return;
        }

        lock (_pools)
        {
            pooled.Leases--;
            pooled.LastUsed = DateTime.UtcNow;
            if ((pooled.Broken || !pooled.Client.IsConnected) && pooled.Leases == 0)
            {
                foreach (var list in _pools.Values)
                {
                    list.Remove(pooled);
                }

                pooled.Dispose();
            }
        }
    }

    private void Sweep()
    {
        List<Pooled> idle;
        lock (_pools)
        {
            idle = _pools.Values.SelectMany(l => l)
                .Where(p => p.Leases == 0 && (p.Broken || !p.Client.IsConnected || DateTime.UtcNow - p.LastUsed > IdleFor)).ToList();
            foreach (var list in _pools.Values)
            {
                list.RemoveAll(idle.Contains);
            }

            foreach (var key in _pools.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            {
                _pools.Remove(key);
            }
        }

        foreach (var p in idle)
        {
            p.Dispose();
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        List<Pooled> all;
        lock (_pools)
        {
            _disposed = true;
            all = _pools.Values.SelectMany(l => l).ToList();
            _pools.Clear();
        }

        foreach (var p in all)
        {
            p.Dispose();
        }
    }

    private sealed class Pooled(SshClient client, bool pooled)
    {
        public SshClient Client { get; } = client;
        public bool InPool { get; } = pooled;
        public SemaphoreSlim Connecting { get; } = new(1, 1);
        public int Leases { get; set; }
        public bool Broken { get; set; }

        /// <summary>Connected once: when it is not connected any more, it dropped.</summary>
        public bool Ready { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public bool Dispose()
        {
            try { Client.Dispose(); } catch { /* best effort */ }
            return true;
        }
    }

    /// <summary>A session on a shared connection: it takes one at <see cref="ConnectAsync"/> and gives it back when disposed.</summary>
    private sealed class PooledSession(SshConnectionPool pool, string key, SshConnectionSettings settings) : IHostSession
    {
        private Pooled? _lease;

        public async Task ConnectAsync(CancellationToken ct) => _lease ??= await pool.LeaseAsync(key, settings, ct);

        public IHostCommand CreateCommand(string command)
        {
            var lease = _lease ?? throw new InvalidOperationException("Not connected.");
            try
            {
                return new Command(new SshHostCommand(lease.Client.CreateCommand(command)), lease);
            }
            catch (Exception ex) when (ex is SshConnectionException or ObjectDisposedException or SshException)
            {
                lease.Broken = true;
                throw;
            }
        }

        public void Dispose()
        {
            if (_lease is { } lease)
            {
                _lease = null;
                pool.Return(lease);
            }
        }

        /// <summary>A command that marks its connection broken when the connection fails under it, so the next session gets a new one.</summary>
        private sealed class Command(SshHostCommand inner, Pooled lease) : IHostCommand
        {
            public TimeSpan CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
            public int? ExitStatus => inner.ExitStatus;
            public string Error => inner.Error;
            public string Result => inner.Result;
            public Stream OutputStream => inner.OutputStream;
            public bool BufferOutput { get => inner.BufferOutput; set => inner.BufferOutput = value; }

            public string Execute() => Guard(inner.Execute);

            public async Task ExecuteAsync(CancellationToken ct)
            {
                try
                {
                    await inner.ExecuteAsync(ct);
                }
                catch (Exception ex) when (ex is SshConnectionException or ObjectDisposedException)
                {
                    lease.Broken = true;
                    throw;
                }
            }

            public IAsyncResult BeginExecute() => Guard(inner.BeginExecute);
            public Stream CreateInputStream() => inner.CreateInputStream();
            public void EndExecute(IAsyncResult asyncResult) => Guard(() => { inner.EndExecute(asyncResult); return 0; });
            public void Dispose() => inner.Dispose();

            private T Guard<T>(Func<T> run)
            {
                try
                {
                    return run();
                }
                catch (Exception ex) when (ex is SshConnectionException or ObjectDisposedException)
                {
                    lease.Broken = true;
                    throw;
                }
            }
        }
    }
}
