using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A hung adb server is ended only after "adb devices" times out round after round, and only if it is adb.</summary>
public sealed class AdbHangGuardTests
{
    private readonly List<int> _killed = [];
    private int? _pid = 21588;
    private bool _isAdb = true;

    private AdbHangGuard Make() => new(NullLogger.Instance)
    {
        ServerPid = _ => Task.FromResult(_pid),
        Kill = pid => { if (!_isAdb) return false; _killed.Add(pid); return true; },
    };

    [Fact]
    public async Task OneSlowRoundIsNotAHang()
    {
        var g = Make();
        Assert.False(await g.ReportAsync(true, default));
        Assert.False(await g.ReportAsync(false, default));
        Assert.False(await g.ReportAsync(true, default)); // the count started again
        Assert.Empty(_killed);
    }

    [Fact]
    public async Task RoundsInARowEndTheServer()
    {
        var g = Make();
        for (var i = 1; i < AdbHangGuard.Limit; i++) Assert.False(await g.ReportAsync(true, default));
        Assert.True(await g.ReportAsync(true, default));
        Assert.Equal([21588], _killed);
        // the new server gets the same patience
        Assert.False(await g.ReportAsync(true, default));
    }

    [Fact]
    public async Task SomethingElseOnThePortIsLeftAlone()
    {
        _isAdb = false;
        var g = Make();
        for (var i = 1; i < AdbHangGuard.Limit; i++) await g.ReportAsync(true, default);
        Assert.False(await g.ReportAsync(true, default));
        Assert.Empty(_killed);
    }

    [Fact]
    public async Task NoListenerNothingEnded()
    {
        _pid = null;
        var g = Make();
        for (var i = 1; i < AdbHangGuard.Limit; i++) await g.ReportAsync(true, default);
        Assert.False(await g.ReportAsync(true, default));
    }

    [Fact]
    public void TheListenerIsReadFromNetstat()
    {
        const string netstat = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1234
              TCP    127.0.0.1:5037         127.0.0.1:50123        ESTABLISHED     21588
              TCP    127.0.0.1:15037        0.0.0.0:0              LISTENING       999
              TCP    127.0.0.1:5037         0.0.0.0:0              LISTENING       21588
            """;
        Assert.Equal(21588, AdbHangGuard.ParseListener(netstat.Replace("\n", "\r\n"), 5037));
        Assert.Null(AdbHangGuard.ParseListener(netstat, 5038));
        Assert.Null(AdbHangGuard.ParseListener("", 5037));
    }
}
