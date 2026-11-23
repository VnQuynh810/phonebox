using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The phone mic and Plugcam stay off until the user presses their Set up button in the settings page; nothing here
/// runs on its own. Setting up the mic checks that a playback device to play into exists (a virtual cable cannot be
/// installed for the user: it is a driver), fetches the official scrcpy-server 5.0.1 from the scrcpy release on
/// GitHub when no copy is found (checked against its SHA-256 before it is kept), and turns the mic on. Setting up
/// Plugcam finds plugcam.exe and only then lets the agent start and watch it.
/// </summary>
public static class PhoneDeviceSetup
{
    public const string ServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1";
    public const string ServerFileName = "scrcpy-server-5.0.1";
    public const string CableUrl = "https://vb-audio.com/Cable/";

    public sealed record Result(bool Ok, string Message, string? Version = null);

    /// <summary>Where the downloaded server is kept: next to the settings file, so updating the agent keeps it.</summary>
    public static string ManagedServerPath(ConfigStore cfg) => Path.Combine(Path.GetDirectoryName(cfg.Path) ?? AppPaths.NeutralCwd, ServerFileName);

    /// <param name="playbackDevices">Names of the active playback devices.</param>
    /// <param name="download">Fetches a URL's bytes (null in tests that must not reach the network).</param>
    public static Task<Result> SetupMicAsync(ConfigStore cfg, Func<IReadOnlyList<string>> playbackDevices,
        Func<string, CancellationToken, Task<byte[]>> download, CancellationToken ct) =>
        SetupMicAsync(cfg, playbackDevices, download, ScrcpyAudio.ServerSha256, ct);

    /// <param name="sha256">The server's expected hash (tests pass the hash of their own stand-in file).</param>
    internal static async Task<Result> SetupMicAsync(ConfigStore cfg, Func<IReadOnlyList<string>> playbackDevices,
        Func<string, CancellationToken, Task<byte[]>> download, string sha256, CancellationToken ct)
    {
        var c = cfg.Get().PhoneMic;
        var endpoint = string.IsNullOrWhiteSpace(c.Endpoint) ? new PhoneMicConfig().Endpoint : c.Endpoint;
        if (!playbackDevices().Any(d => d.Contains(endpoint, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"No playback device named like \"{endpoint}\". Install a virtual cable such as VB-CABLE ({CableUrl}), restart the computer, then press Set up again.");

        var have = new[] { c.ServerPath, ManagedServerPath(cfg), Path.Combine(AppContext.BaseDirectory, "scrcpy-server") }
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && Sha256Of(p) == sha256);
        string? serverPath = c.ServerPath;
        if (have is null)
        {
            byte[] bytes;
            try { bytes = await download(ServerUrl, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { return new(false, "Could not download scrcpy-server 5.0.1 from GitHub: " + ex.Message); }
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                return new(false, "The downloaded scrcpy-server is not the official 5.0.1 (SHA-256 differs); nothing was kept.");
            var target = ManagedServerPath(cfg);
            var tmp = target + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, target, overwrite: true);
            serverPath = null; // found next to the settings file without a path
        }
        else if (have != c.ServerPath) serverPath = null;

        return Save(cfg, x => { x.PhoneMic.Enabled = true; x.PhoneMic.ServerPath = serverPath; },
            "Phone mic is on. Plug the phone in over USB; apps pick the cable's other end (for VB-CABLE: CABLE Output) as their microphone.");
    }

    /// <summary>Finds plugcam.exe (the configured path, a running copy, then the usual install folders) and lets the agent manage it.</summary>
    public static Result SetupPlugcam(ConfigStore cfg, Func<IEnumerable<string>> runningPaths, Func<string, bool> exists)
    {
        var configured = cfg.Get().Plugcam.Path;
        var found = new[] { configured }.Concat(runningPaths()).Concat(InstallCandidates())
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && exists(p!));
        if (found is null)
            return new(false, "plugcam.exe was not found. Start Plugcam once, or type the path to plugcam.exe, then press Set up again.");
        return Save(cfg, x => { x.Plugcam.Manage = true; x.Plugcam.Path = found; },
            $"The agent now starts Plugcam ({found}) and restarts it if it crashes. Turn off Launch at login in Plugcam so it is not started twice.");
    }

    internal static IEnumerable<string> InstallCandidates()
    {
        foreach (var root in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var dir = Environment.GetFolderPath(root);
            if (string.IsNullOrEmpty(dir)) continue;
            yield return Path.Combine(dir, "Programs", "Plugcam", "plugcam.exe");
            yield return Path.Combine(dir, "Plugcam", "plugcam.exe");
        }
    }

    /// <summary>The names of the active playback devices.</summary>
    public static IReadOnlyList<string> PlaybackDevices() =>
#if WINDOWS
        WasapiPcmOutput.PlaybackDeviceNames();
#else
        [];
#endif

    /// <summary>The full paths of running plugcam.exe processes (ones this user can read).</summary>
    public static IEnumerable<string> RunningPlugcam()
    {
        foreach (var p in Process.GetProcessesByName("plugcam"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            p.Dispose();
            if (path is not null) yield return path;
        }
    }

    private static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private static Result Save(ConfigStore cfg, Action<AgentConfig> change, string message)
    {
        // a copy: cfg.Get() is the shared, cached config, and the save may still refuse
        var version = cfg.Version;
        var copy = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts)!;
        change(copy);
        var err = cfg.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts), version, out var saved);
        return err is null ? new(true, message, saved.ToString(CultureInfo.InvariantCulture)) : new(false, err);
    }
}
