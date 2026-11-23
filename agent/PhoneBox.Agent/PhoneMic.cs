using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>Plays PCM from a <see cref="PcmJitterBuffer"/> on one audio device until disposed or the device goes away.</summary>
public interface IPcmOutput : IDisposable
{
    string DeviceName { get; }
    /// <summary>True once playback stopped on its own (device unplugged, disabled, driver reset): a new output is needed.</summary>
    bool Faulted { get; }
}

/// <summary>
/// The phone's microphone as a computer microphone, without scrcpy.exe: the agent pushes scrcpy-server 5.0.1 to the
/// phone (only the official file, checked by SHA-256), starts it audio-only with raw PCM, reads it through an adb forward
/// on 127.0.0.1 and plays it into a playback device picked by name, normally VB-Cable's "CABLE Input", so apps use
/// "CABLE Output" as their mic. Reconnects by itself: when the phone is unplugged, and when Plugcam starts its camera
/// (it stops every scrcpy server on the phone, this one included). Muting keeps the connection and plays silence.
/// </summary>
public sealed class PhoneMic : BackgroundService
{
    public const string ActionId = "builtin:phone-mic";
    internal const int ReadTimeoutMs = 2500;

    private readonly ConfigStore _cfg;
    private readonly AdbReverse? _adb;
    private readonly ILogger<PhoneMic> _log;
    private readonly Func<string, int, PcmJitterBuffer, IPcmOutput>? _outputFactory;
    private readonly HashSet<string> _pushed = []; // phones that have the checked server at RemotePath (this run)
    private string? _verifiedServer;               // local server file whose hash matched, "path|size|mtime"

    public PcmJitterBuffer Buffer { get; } = new();

    public PhoneMic(ConfigStore cfg, AdbReverse adb, ILogger<PhoneMic> log) : this(cfg, adb, log, DefaultOutput()) { }

    internal PhoneMic(ConfigStore cfg, AdbReverse? adb, ILogger<PhoneMic> log, Func<string, int, PcmJitterBuffer, IPcmOutput>? output)
    {
        _cfg = cfg; _adb = adb; _log = log; _outputFactory = output;
        Buffer.Muted = cfg.Get().PhoneMic.StartMuted;
    }

    private static Func<string, int, PcmJitterBuffer, IPcmOutput>? DefaultOutput()
    {
#if WINDOWS
        return (name, latency, buffer) => new WasapiPcmOutput(name, latency, buffer);
#else
        return null;
#endif
    }

    // ------------------------------------------------------------ state for the phone and the settings page
    private volatile string _state = "off";
    private volatile string? _problem;
    private volatile string? _outputDevice;
    private volatile string? _outputProblem;
    private long _liveSince, _lostAt;
    public int Reconnects { get; private set; }
    /// <summary>How long the last reconnect took, from losing the stream to audio again (ms), or -1.</summary>
    public long LastRecoveryMs { get; private set; } = -1;

    /// <summary>off, waiting (no phone), connecting, live or error.</summary>
    public string State => _state;
    public bool Muted => Buffer.Muted;

    public bool Toggle()
    {
        Buffer.Muted = !Buffer.Muted;
        _log.LogInformation("phone mic {State}", Buffer.Muted ? "muted" : "unmuted");
        return Buffer.Muted;
    }

    public void SetMuted(bool muted) => Buffer.Muted = muted;

    /// <summary>The state in a few words (settings page, phone).</summary>
    public string StateText => _state switch
    {
        "off" => "Off",
        "waiting" => "No phone",
        "connecting" => "Connecting…",
        "error" => "Error",
        _ when _outputDevice is null => "No output device",
        _ => Buffer.Muted ? "Muted" : "Live",
    };

    public bool Enabled => _cfg.Get().PhoneMic.Enabled && _outputFactory is not null;

    /// <summary>Loudest recent level in dBFS (-90 for silence), from what was played in the last second or two.</summary>
    public int LevelDb => Buffer.HeldPeak is var p and > 0 ? (int)Math.Round(Math.Max(-90, 20 * Math.Log10(p))) : -90;

    /// <summary>What the phone's /api/state carries (null while the mic is off): enough for a Custom HTML widget.</summary>
    public object? PhoneState() => !Enabled ? null : new
    {
        state = _state,
        text = StateText,
        muted = Buffer.Muted,
        live = _state == "live" && _outputDevice is not null,
        level = LevelDb,
        output = _outputDevice,
        reconnects = Reconnects,
        recoveryMs = LastRecoveryMs,
    };

    public object Snapshot() => new
    {
        enabled = _cfg.Get().PhoneMic.Enabled,
        level = LevelDb,
        state = _state,
        text = StateText,
        muted = Buffer.Muted,
        problem = _problem,
        output = _outputDevice,
        outputProblem = _outputProblem,
        bufferedMs = Buffer.BufferedMs,
        peak = Math.Round(Buffer.Peak, 3),
        underruns = Buffer.Underruns,
        droppedMs = Buffer.DroppedBytes / ScrcpyAudio.BytesPerMs,
        reconnects = Reconnects,
        lastRecoveryMs = LastRecoveryMs,
        liveForS = _state == "live" ? (Environment.TickCount64 - Interlocked.Read(ref _liveSince)) / 1000 : 0,
    };

    private void SetState(string state, string? problem = null)
    {
        if (state == _state && problem == _problem) return;
        _state = state; _problem = problem;
        if (problem is not null) _log.LogWarning("phone mic: {State}: {Problem}", state, problem);
    }

    // ------------------------------------------------------------ the loops
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_outputFactory is null) { SetState("off", null); return; } // not on this platform
        var output = Task.Run(() => OutputLoop(ct), ct);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var c = _cfg.Get().PhoneMic;
            if (!c.Enabled) { SetState("off"); failures = 0; await Delay(1000, ct); continue; }
            if (AdbReverse.SessionEnding) { await Delay(2000, ct); continue; }
            var adb = AdbReverse.FindAdb(_cfg.Get().AdbPath);
            var serial = _adb?.DeviceList.FirstOrDefault();
            if (adb is null || serial is null)
            {
                SetState("waiting", adb is null ? "adb not found" : null);
                await Delay(700, ct);
                continue;
            }
            var wasLive = false;
            try { wasLive = await RunSession(adb, serial, c, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { SetState("error", ex.Message); }
            Buffer.Clear();
            if (_state == "live") SetState("connecting");
            // recovery is timed from the first loss, across the failed attempts that may follow it
            if (wasLive || Interlocked.Read(ref _lostAt) == 0) Interlocked.Exchange(ref _lostAt, Environment.TickCount64);
            // a stream that ran retries at once (Plugcam's restart, a cable wiggle); one that never started backs off
            failures = wasLive ? 0 : failures + 1;
            if (failures >= 3) ForgetPhone(serial); // its copy of the server may be gone (phone reset, storage cleared)
            // Plugcam stops every scrcpy server on the phone each time it (re)starts its camera, ours included, then waits
            // 3 s (STALE_SERVER_GRACE in its scrcpy/server.rs) before starting its own: starting again at once lands in
            // that pause, where nothing stops it a second time. Its reconnect after an unexpected loss can sweep once or
            // twice more, so the first few retries stay quick too; only a server that keeps failing backs off.
            await Delay(failures == 0 ? 50 : failures <= 6 ? 100 : Math.Min(5000, 100 << Math.Min(failures - 6, 6)), ct);
        }
        try { await output; } catch (OperationCanceledException) { }
    }

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    /// <summary>Keeps one output open on the configured device while the mic is on; reopens it when it faults.</summary>
    private async Task OutputLoop(CancellationToken ct)
    {
        IPcmOutput? output = null;
        string? name = null;
        int latency = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var c = _cfg.Get().PhoneMic;
                Buffer.Configure(c.LatencyMs, c.MaxBufferMs, c.Gain);
                var want = c.Enabled ? (c.Endpoint ?? "").Trim() : "";
                if (output is not null && (output.Faulted || want != name || c.LatencyMs != latency))
                {
                    if (output.Faulted) _log.LogWarning("phone mic: playback on {Device} stopped; reopening", output.DeviceName);
                    output.Dispose(); output = null; _outputDevice = null;
                }
                if (output is null && want.Length > 0)
                {
                    try
                    {
                        output = _outputFactory!(want, Math.Clamp(c.LatencyMs, 10, 500), Buffer);
                        name = want; latency = c.LatencyMs;
                        _outputDevice = output.DeviceName; _outputProblem = null;
                        _log.LogInformation("phone mic: playing into {Device}", output.DeviceName);
                    }
                    catch (Exception ex)
                    {
                        if (_outputProblem != ex.Message) _log.LogWarning("phone mic: no output ({Message})", ex.Message);
                        _outputProblem = ex.Message;
                    }
                }
                await Delay(output is null ? 3000 : 1000, ct);
            }
        }
        finally { output?.Dispose(); _outputDevice = null; }
    }

    /// <summary>One server run: push if needed, start, connect, stream until it ends. True if audio flowed.</summary>
    private async Task<bool> RunSession(string adb, string serial, PhoneMicConfig c, CancellationToken ct)
    {
        var source = ScrcpyAudio.MicSources.Contains(c.Source) ? c.Source : "mic-camcorder";
        SetState("connecting", _problem);
        await EnsureServer(adb, serial, c, ct);

        var scid = RandomNumberGenerator.GetInt32(0, int.MaxValue);
        var port = UsbLink.FreeLoopbackPort();
        await AdbReverse.Adb(adb, $"-s {serial} forward tcp:{port} localabstract:{ScrcpyAudio.SocketName(scid)}", ct);
        Process? server = null;
        var started = Environment.TickCount64;
        try
        {
            server = StartServer(adb, serial, scid, source);
            using var socket = await Connect(port, server, ct);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var meta = new byte[ScrcpyAudio.MetaLength];
            await ReadExactly(stream, meta, ct);
            var (device, codec) = ScrcpyAudio.ParseMeta(meta);
            if (ScrcpyAudio.CodecProblem(codec) is { } problem) throw new IOException(problem);

            var lost = Interlocked.Exchange(ref _lostAt, 0);
            if (lost != 0) { LastRecoveryMs = Environment.TickCount64 - lost; Reconnects++; }
            Interlocked.Exchange(ref _liveSince, Environment.TickCount64);
            SetState("live");
            _log.LogInformation("phone mic: live from {Device} ({Source}) after {Ms} ms{Recovery}", device, source,
                Environment.TickCount64 - started, lost != 0 ? $", {LastRecoveryMs} ms after the stream was lost" : "");

            var header = new byte[ScrcpyAudio.HeaderLength];
            var payload = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && _cfg.Get().PhoneMic.Enabled && _cfg.Get().PhoneMic.Source == c.Source)
            {
                if (!await ReadExactly(stream, header, ct, eofOk: true)) { _log.LogInformation("phone mic: the stream ended"); break; }
                var h = ScrcpyAudio.ParseHeader(header);
                if (h.Length > payload.Length) payload = new byte[h.Length];
                await ReadExactly(stream, payload.AsMemory(0, h.Length), ct);
                if (!h.Config) Buffer.Write(payload.AsSpan(0, h.Length));
            }
            return true;
        }
        catch (IOException) when (_state == "live")
        {
            _log.LogInformation("phone mic: the stream was cut (phone unplugged, or Plugcam restarted its camera)");
            return true;
        }
        finally
        {
            if (server is not null)
            {
                // Never kill a live "adb shell": on this computer that left the adb server hung (every adb command,
                // Plugcam's too, then waits forever). The socket is closed by now, so the phone's server ends on its
                // next write and the shell with it; one that never accepted is ended on the phone instead.
                if (!await Exited(server, 1500))
                {
                    try { await AdbReverse.Adb(adb, $"-s {serial} shell pkill -f '[s]cid={scid:x8}'", CancellationToken.None, anyExit: true); } catch { }
                    if (!await Exited(server, 3000))
                    {
                        _log.LogWarning("phone mic: the adb shell did not end; stopping it");
                        try { server.Kill(); } catch { }
                    }
                }
                server.Dispose();
            }
            try { await AdbReverse.Adb(adb, $"-s {serial} forward --remove tcp:{port}", CancellationToken.None, anyExit: true); } catch { }
        }
    }

    private static async Task<bool> Exited(Process p, int ms)
    {
        try
        {
            using var wait = new CancellationTokenSource(ms);
            await p.WaitForExitAsync(wait.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return true; }
    }

    private Process StartServer(string adb, string serial, int scid, string source)
    {
        var psi = new ProcessStartInfo(adb)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        foreach (var a in new[] { "-s", serial, "shell", ScrcpyAudio.ServerCommand(scid, source) }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new IOException("could not start adb");
        // not in the agent's JobObject (see RunSession's cleanup): if the agent dies, its socket closes, the phone's
        // server stops on its next write and this shell ends by itself
        void Log(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Exception", StringComparison.Ordinal))
                _log.LogWarning("phone mic server: {Line}", line.Trim());
            else _log.LogDebug("phone mic server: {Line}", line.Trim());
        }
        p.OutputDataReceived += (_, e) => Log(e.Data);
        p.ErrorDataReceived += (_, e) => Log(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    /// <summary>
    /// adb accepts a forward at once and closes it when nothing listens on the phone yet, so a connection counts only
    /// once the server's dummy byte arrives.
    /// </summary>
    private static async Task<Socket> Connect(int port, Process server, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 8000;
        var one = new byte[1];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited) throw new IOException("the phone's scrcpy server exited before accepting");
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(1500);
                if (await socket.ReceiveAsync(one, SocketFlags.None, wait.Token) == 1) return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { }
            socket.Dispose();
            if (Environment.TickCount64 > deadline) throw new IOException("the phone's scrcpy server did not answer");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Reads exactly the buffer; false on a clean end before the first byte when <paramref name="eofOk"/>.</summary>
    private static async Task<bool> ReadExactly(Stream stream, Memory<byte> buffer, CancellationToken ct, bool eofOk = false)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ReadTimeoutMs);
            int n;
            try { n = await stream.ReadAsync(buffer[read..], wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("no audio from the phone for a while"); }
            if (n == 0)
            {
                if (read == 0 && eofOk) return false;
                throw new IOException("the stream ended");
            }
            read += n;
        }
        return true;
    }

    // ------------------------------------------------------------ the server file
    /// <summary>The local scrcpy-server: the configured file, else "scrcpy-server" next to the agent or the config.</summary>
    internal string? ServerFile(PhoneMicConfig c) =>
        !string.IsNullOrWhiteSpace(c.ServerPath) ? c.ServerPath
            : new[] { PhoneDeviceSetup.ManagedServerPath(_cfg), Path.Combine(AppContext.BaseDirectory, "scrcpy-server"), Path.Combine(Path.GetDirectoryName(_cfg.Path) ?? "", "scrcpy-server") }
                .FirstOrDefault(File.Exists);

    internal static bool IsOfficialServer(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f)) == ScrcpyAudio.ServerSha256;
    }

    private async Task EnsureServer(string adb, string serial, PhoneMicConfig c, CancellationToken ct)
    {
        if (_pushed.Contains(serial)) return;
        var path = ServerFile(c);
        if (path is null || !File.Exists(path))
            throw new FileNotFoundException("scrcpy-server 5.0.1 not found: press Set up in the phone mic settings");
        var info = new FileInfo(path);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (_verifiedServer != key)
        {
            if (!IsOfficialServer(path)) throw new InvalidDataException("the scrcpy-server file is not the official 5.0.1 (SHA-256 differs)");
            _verifiedServer = key;
        }
        var remote = await AdbReverse.Adb(adb, $"-s {serial} shell sha256sum {ScrcpyAudio.RemotePath}", ct, anyExit: true);
        if (!remote.StartsWith(ScrcpyAudio.ServerSha256, StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo(adb);
            foreach (var a in new[] { "-s", serial, "push", path, ScrcpyAudio.RemotePath }) psi.ArgumentList.Add(a);
            var r = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(30), ct);
            if (r.ExitCode != 0) throw new IOException("could not copy scrcpy-server to the phone");
            _log.LogInformation("phone mic: scrcpy-server copied to {Device}", serial);
        }
        _pushed.Add(serial);
    }

    /// <summary>The phone was unplugged or replaced: check its copy again next time.</summary>
    internal void ForgetPhone(string serial) => _pushed.Remove(serial);
}
