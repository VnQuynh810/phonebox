namespace PhoneBox.Agent;

/// <summary>
/// The phone mic and Plugcam as seen from the phone: their state in /api/state (for a Custom HTML widget such as
/// extras/phone-mic/mic-camera-widget.html) and the shortcut commands that act on them, so the panel needs no new widget or app build:
/// <list type="bullet">
/// <item><c>builtin:phone-mic</c>: toggles mute (args "mute" / "unmute" set it); as a toggle key it shows on while live;</item>
/// <item><c>builtin:plugcam-start</c>: starts Plugcam if it is not running (also after it was quit from its tray);</item>
/// <item><c>builtin:plugcam-restart</c>: ends Plugcam and starts it again (a frozen webcam);</item>
/// <item><c>builtin:plugcam-stop</c>: ends Plugcam and keeps it closed until <c>builtin:plugcam-start</c>.</item>
/// </list>
/// Set once by Program; null in tests and on platforms without them.
/// </summary>
public static class PhoneDevices
{
    public const string MicCmd = "builtin:phone-mic", PlugcamStartCmd = "builtin:plugcam-start", PlugcamRestartCmd = "builtin:plugcam-restart", PlugcamStopCmd = "builtin:plugcam-stop";

    public static PhoneMic? Mic { get; set; }
    public static Plugcam? Plugcam { get; set; }

    public static bool IsCommand(string? cmd) => cmd is MicCmd or PlugcamStartCmd or PlugcamRestartCmd or PlugcamStopCmd;

    /// <summary>Runs one of the commands; throws with a message for the phone when it cannot.</summary>
    public static void Run(string cmd, string? args)
    {
        switch (cmd)
        {
            case MicCmd:
                if (Mic is not { Enabled: true } mic) throw new InvalidOperationException("the phone mic is off in the agent's settings");
                switch ((args ?? "").Trim().ToLowerInvariant())
                {
                    case "mute": mic.SetMuted(true); break;
                    case "unmute": mic.SetMuted(false); break;
                    default: mic.Toggle(); break;
                }
                break;
            case PlugcamStartCmd:
                if (Plugcam is not { Managed: true } start) throw new InvalidOperationException("Plugcam is not managed by the agent");
                start.Start();
                break;
            case PlugcamRestartCmd:
                if (Plugcam is not { Managed: true } cam) throw new InvalidOperationException("Plugcam is not managed by the agent");
                _ = Task.Run(cam.Restart); // ending it can take a few seconds; the phone must not wait
                break;
            case PlugcamStopCmd:
                if (Plugcam is not { Managed: true } stop) throw new InvalidOperationException("Plugcam is not managed by the agent");
                _ = Task.Run(stop.Stop);
                break;
        }
    }

    /// <summary>A toggle key's state: the mic counts as on while it is live and not muted.</summary>
    public static bool? MicOn => Mic is { Enabled: true } m ? m.State == "live" && !m.Muted : null;
}
