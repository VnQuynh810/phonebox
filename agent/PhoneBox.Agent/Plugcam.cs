using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Plugcam (the phone as a webcam) under the agent's care, so only the agent starts at sign-in: started when it is not
/// running (found by process name, so a copy started by hand or by Plugcam's own "Launch at login" is left alone),
/// restarted when it ends abnormally, with growing pauses if it keeps failing. Quitting it from its tray menu (exit
/// code 0) is respected until it is started again from the phone. It is not tied to the agent's lifetime: restarting
/// or updating the agent does not interrupt the camera.
/// </summary>
public sealed class Plugcam(ConfigStore cfg, ILogger<Plugcam> log) : BackgroundService
{
    public const string ActionId = "builtin:plugcam";
    internal static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60, 120];
    internal const long StableMs = 5 * 60_000; // ran this long: its next failure starts the pauses afresh

    private Process? _child;     // the copy this agent started or watches
    private int _failures;
    private long _nextStart;     // TickCount64 before which no start is tried
    private bool _userQuit;
    private volatile string _state = "off";
    private volatile string? _problem;

    internal Func<string, Process[]> FindProcesses { get; set; } = Process.GetProcessesByName;
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;
    internal Action<Process> EndProcess { get; set; } = p => { p.Kill(); p.WaitForExit(5000); };

    /// <summary>off (not managed), running, starting, stopped (quit by the user), waiting (after a crash) or error.</summary>
    public string State => _state;
    public bool Running => _state == "running";

    public string StateText => _state switch
    {
        "running" => "Running",
        "starting" => "Starting…",
        "stopped" => "Stopped",
        "waiting" => "Restarting soon",
        "error" => "Error",
        _ => "Not managed",
    };

    public object Snapshot() => new { manage = cfg.Get().Plugcam.Manage, state = _state, text = StateText, problem = _problem, failures = _failures };

    public bool Managed => cfg.Get().Plugcam.Manage && !string.IsNullOrWhiteSpace(cfg.Get().Plugcam.Path);

    /// <summary>What the phone's /api/state carries (null while the agent does not manage Plugcam).</summary>
    public object? PhoneState() => !Managed ? null : new { state = _state, text = StateText, running = _state == "running" };

    private bool _restarting;

    /// <summary>
    /// builtin:plugcam-restart: ends Plugcam and starts it again at once (a frozen or black webcam). Only
    /// Plugcam itself is ended, not its adb children: ending a live "adb shell" can hang the adb server; the new
    /// Plugcam stops the old camera server on the phone, which ends that shell.
    /// </summary>
    public bool Restart()
    {
        Process? child;
        lock (_gate)
        {
            if (!cfg.Get().Plugcam.Manage) return false;
            child = _child;
            _userQuit = false; _nextStart = 0;
            if (child is not null) { _restarting = true; _state = "starting"; }
        }
        if (child is not null)
        {
            try { EndProcess(child); } catch { }
            log.LogInformation("Plugcam restarted on request");
        }
        Tick();
        return true;
    }

    /// <summary>
    /// builtin:plugcam-stop: ends Plugcam (also a copy started by hand) and keeps it closed, as quitting it from its
    /// tray does, until builtin:plugcam-start. Its adb children are left to end on their own, as in <see cref="Restart"/>.
    /// </summary>
    public bool Stop()
    {
        var ending = new List<Process>();
        lock (_gate)
        {
            var c = cfg.Get().Plugcam;
            if (!c.Manage || string.IsNullOrWhiteSpace(c.Path)) return false;
            _userQuit = true; _restarting = false; _nextStart = 0;
            if (_child is not null) ending.Add(_child);
            _child = null;
            ending.AddRange(FindProcesses(ProcessName(c.Path)).Where(p => !ending.Any(e => SameProcess(e, p))));
            _state = "stopped"; _problem = null;
        }
        foreach (var p in ending)
        {
            try { EndProcess(p); } catch { }
            p.Dispose();
        }
        log.LogInformation("Plugcam stopped on request; it stays closed until started again (builtin:plugcam-start)");
        return true;
    }

    private static bool SameProcess(Process a, Process b)
    {
        try { return a.Id == b.Id; } catch { return false; }
    }

    internal static string ProcessName(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>builtin:plugcam-start: starts Plugcam now if it is not running (also after the user quit it).</summary>
    public string Start()
    {
        _userQuit = false; _failures = 0; _nextStart = 0;
        Tick();
        return StateText;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _state = "error"; _problem = ex.Message; log.LogWarning("Plugcam: {Message}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private readonly Lock _gate = new();

    internal void Tick()
    {
        lock (_gate)
        {
            var c = cfg.Get().Plugcam;
            if (!c.Manage || string.IsNullOrWhiteSpace(c.Path)) { _state = "off"; _problem = null; return; }
            if (AdbReverse.SessionEnding) return;

            if (_child is not null)
            {
                bool exited;
                try { exited = _child.HasExited; } catch { exited = true; }
                if (!exited) { _state = "running"; return; }
                int? code = null;
                try { code = _child.ExitCode; } catch { }
                long ran = 0;
                try { ran = (long)(DateTime.Now - _child.StartTime).TotalMilliseconds; } catch { }
                _child.Dispose(); _child = null;
                if (_restarting) _restarting = false; // ended on purpose: start it again right below
                else
                {
                    if (code == 0)
                    {
                        _userQuit = true; _state = "stopped";
                        log.LogInformation("Plugcam was closed; it stays closed until started again (builtin:plugcam-start)");
                        return;
                    }
                    if (ran > StableMs) _failures = 0;
                    var wait = BackoffSeconds[Math.Min(_failures, BackoffSeconds.Length - 1)];
                    _failures++;
                    _nextStart = Clock() + wait * 1000L;
                    _state = "waiting";
                    log.LogWarning("Plugcam ended (exit code {Code}); starting it again in {Seconds} s", code?.ToString() ?? "unknown", wait);
                    return;
                }
            }

            // already running (started by hand, by its own launch-at-login, or before the agent restarted): watch it
            var running = FindProcesses(ProcessName(c.Path));
            if (running.Length > 0)
            {
                _child = running[0];
                // holding its handle keeps its exit code readable after it ends (a tray "Quit" is code 0)
                try { _ = _child.Handle; } catch { }
                foreach (var p in running.Skip(1)) p.Dispose();
                _userQuit = false;
                _state = "running"; _problem = null;
                return;
            }
            if (_userQuit) { _state = "stopped"; return; }
            if (Clock() < _nextStart) { _state = "waiting"; return; }
            if (!File.Exists(c.Path)) { _state = "error"; _problem = "plugcam.exe not found at the configured path"; return; }

            var psi = new ProcessStartInfo(c.Path)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(c.Path) ?? AppPaths.NeutralCwd,
            };
            foreach (var a in (c.Args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            // deliberately not in the agent's JobObject: the camera must outlive an agent restart
            _child = Process.Start(psi);
            _state = _child is null ? "error" : "starting";
            _problem = _child is null ? "Plugcam did not start" : null;
            log.LogInformation("Plugcam started");
        }
    }
}
