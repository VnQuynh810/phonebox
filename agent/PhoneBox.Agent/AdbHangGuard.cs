using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Clears an adb server that stopped answering. On Windows the shared adb server (127.0.0.1:5037) can hang for good,
/// seen when the agent restarts while the phone's panel is connected through "adb reverse": every adb command, the
/// agent's and other tools' (Plugcam, scrcpy), then waits forever, so the panel, the phone mic and the webcam all stop.
/// "adb devices" never touches the phone, so when it times out <see cref="Limit"/> rounds in a row the server is hung,
/// not slow: its process is ended (only if it is adb), and the next adb command starts a fresh one. Phones stay
/// attached; reverse and forward rules are set again by their owners.
/// </summary>
public sealed class AdbHangGuard(ILogger log)
{
    public const int Limit = 2;
    private int _hung;

    internal Func<CancellationToken, Task<int?>> ServerPid { get; set; } = FindServerPid;
    internal Func<int, bool> Kill { get; set; } = KillIfAdb;

    /// <summary>The result of one round's "adb devices"; true when a hung server was ended.</summary>
    public async Task<bool> ReportAsync(bool timedOut, CancellationToken ct)
    {
        if (!timedOut) { _hung = 0; return false; }
        if (++_hung < Limit) return false;
        _hung = 0;
        var pid = await ServerPid(ct);
        if (pid is not { } id) { log.LogWarning("adb: the adb server does not answer, and its process was not found"); return false; }
        if (!Kill(id)) { log.LogWarning("adb: the adb server does not answer; process {Pid} on its port is not adb, left alone", id); return false; }
        log.LogWarning("adb: the adb server stopped answering; ended it (process {Pid}), a new one starts with the next adb command", id);
        return true;
    }

    internal static int ServerPort =>
        int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"), out var p) && p is > 0 and < 65536 ? p : 5037;

    /// <summary>The process listening on 127.0.0.1:<paramref name="port"/>, from "netstat -ano -p TCP" output.</summary>
    internal static int? ParseListener(string netstat, int port)
    {
        foreach (var line in netstat.Split('\n'))
        {
            var f = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            // TCP    127.0.0.1:5037    0.0.0.0:0    LISTENING    21588
            if (f.Length == 5 && f[0] == "TCP" && f[3] == "LISTENING" && f[1].EndsWith(":" + port, StringComparison.Ordinal)
                && (f[1].StartsWith("127.0.0.1:", StringComparison.Ordinal) || f[1].StartsWith("0.0.0.0:", StringComparison.Ordinal))
                && int.TryParse(f[4], out var pid) && pid > 0)
                return pid;
        }
        return null;
    }

    private static async Task<int?> FindServerPid(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null; // seen on Windows only; elsewhere adb is left as it is
        try
        {
            var r = await ToolProcess.RunAsync(new ProcessStartInfo("netstat", "-ano -p TCP"), TimeSpan.FromSeconds(10), ct);
            return ParseListener(r.Output, ServerPort);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool KillIfAdb(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Equals("adb", StringComparison.OrdinalIgnoreCase)) return false;
            p.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
