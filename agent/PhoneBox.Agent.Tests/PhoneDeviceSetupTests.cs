using System.Security.Cryptography;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class PhoneDeviceSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-setup-{Guid.NewGuid():N}");
    private readonly string _config;
    private static readonly byte[] Server = "stand-in for scrcpy-server"u8.ToArray();
    private static readonly string ServerSha = Convert.ToHexStringLower(SHA256.HashData(Server));

    public PhoneDeviceSetupTests()
    {
        Directory.CreateDirectory(_dir);
        _config = Path.Combine(_dir, "config.json");
        File.WriteAllText(_config, "{}");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Func<IReadOnlyList<string>> Devices(params string[] names) => () => names;

    [Fact]
    public async Task WithoutAVirtualCableNothingIsDownloadedOrTurnedOn()
    {
        var store = new ConfigStore(_config);
        var downloads = 0;
        var r = await PhoneDeviceSetup.SetupMicAsync(store, Devices("Speakers (Realtek(R) Audio)"),
            (_, _) => { downloads++; return Task.FromResult(Server); }, ServerSha, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("VB-CABLE", r.Message);
        Assert.Equal(0, downloads);
        Assert.False(store.Get().PhoneMic.Enabled);
    }

    [Fact]
    public async Task AServerThatIsNotTheOfficialOneIsNotKept()
    {
        var store = new ConfigStore(_config);
        var r = await PhoneDeviceSetup.SetupMicAsync(store, Devices("CABLE Input (VB-Audio Virtual Cable)"),
            (_, _) => Task.FromResult("something else"u8.ToArray()), ServerSha, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(File.Exists(PhoneDeviceSetup.ManagedServerPath(store)));
        Assert.False(store.Get().PhoneMic.Enabled);
    }

    [Fact]
    public async Task SetUpDownloadsTheServerOnceAndTurnsTheMicOn()
    {
        var store = new ConfigStore(_config);
        var urls = new List<string>();
        Task<byte[]> Fetch(string url, CancellationToken _) { urls.Add(url); return Task.FromResult(Server); }

        var r = await PhoneDeviceSetup.SetupMicAsync(store, Devices("CABLE Input (VB-Audio Virtual Cable)"), Fetch, ServerSha, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.Equal([PhoneDeviceSetup.ServerUrl], urls);
        Assert.Equal(Server, File.ReadAllBytes(PhoneDeviceSetup.ManagedServerPath(store)));
        Assert.True(store.Get().PhoneMic.Enabled);
        Assert.Null(store.Get().PhoneMic.ServerPath);

        // pressed again: the kept copy is used, nothing is fetched
        Assert.True((await PhoneDeviceSetup.SetupMicAsync(store, Devices("CABLE Input"), Fetch, ServerSha, CancellationToken.None)).Ok);
        Assert.Single(urls);
    }

    [Fact]
    public void TheMicIsOffInAFreshConfigAndBadValuesAreRepaired()
    {
        File.WriteAllText(_config, """{ "phoneMic": { "source": "rm -rf", "endpoint": " ", "latencyMs": 99999, "gain": 50 } }""");
        var c = new ConfigStore(_config).Get();
        Assert.False(c.PhoneMic.Enabled);
        Assert.Equal("mic-camcorder", c.PhoneMic.Source);
        Assert.Equal("CABLE Input", c.PhoneMic.Endpoint);
        Assert.Equal(500, c.PhoneMic.LatencyMs);
        Assert.Equal(8, c.PhoneMic.Gain);
    }
}
