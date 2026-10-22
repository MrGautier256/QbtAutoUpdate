using System.Net;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class QbtCloserTests
{
    private sealed class FakeInstance : IQbtInstance
    {
        public int Id => 4242;
        public QbtAccess Access { get; init; } = QbtAccess.Controllable;
        public bool HasExited { get; set; }
        /// <summary>What happens when Restart Manager asks: true = qBittorrent exits.</summary>
        public bool ExitsOnRestartManager { get; init; } = true;
        public bool ExitsOnKill { get; init; } = true;
        public int RestartManagerCalls { get; private set; }
        public int KillCalls { get; private set; }

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(HasExited);

        public RestartManagerResult RequestClose(TimeSpan timeout)
        {
            RestartManagerCalls++;
            HasExited = ExitsOnRestartManager;
            // qBittorrent saves inside WM_QUERYENDSESSION, so Restart Manager often reports a failure anyway.
            return RestartManagerResult.Reported;
        }

        public bool Kill()
        {
            KillCalls++;
            HasExited = ExitsOnKill;
            return true;
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static Task<CloseResult> Close(FakeInstance instance, bool allowForceClose = false, Func<WebUiClient?>? webUi = null) =>
        new QbtCloser(NullLog.Instance, webUi ?? (() => null))
            .CloseAsync(instance, webUi is not null, Timeout, allowForceClose, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Restart_manager_closes_and_success_is_the_exit()
    {
        var instance = new FakeInstance();

        CloseResult result = await Close(instance);

        Assert.True(result.Closed);
        Assert.Equal(CloseMethod.RestartManager, result.Method);
        Assert.Equal(1, instance.RestartManagerCalls);
    }

    [Fact]
    public async Task Never_kills_by_default()
    {
        var instance = new FakeInstance { ExitsOnRestartManager = false };

        CloseResult result = await Close(instance);

        Assert.False(result.Closed);
        Assert.Equal(0, instance.KillCalls);
        Assert.Contains("Close qBittorrent to finish the update", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kills_only_with_allow_force_close()
    {
        var instance = new FakeInstance { ExitsOnRestartManager = false };

        CloseResult result = await Close(instance, allowForceClose: true);

        Assert.True(result.Closed);
        Assert.Equal(CloseMethod.Killed, result.Method);
        Assert.Equal(1, instance.KillCalls);
    }

    [Theory]
    [InlineData(QbtAccess.Elevated, "administrator")]
    [InlineData(QbtAccess.OtherUser, "another user")]
    [InlineData(QbtAccess.Unreadable, "inspected")]
    public async Task Does_not_touch_what_it_cannot_close(QbtAccess access, string reason)
    {
        var instance = new FakeInstance { Access = access };

        CloseResult result = await Close(instance, allowForceClose: true);

        Assert.False(result.Closed);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, instance.RestartManagerCalls);
        Assert.Equal(0, instance.KillCalls);
    }

    [Fact]
    public async Task An_already_exited_process_is_fine()
    {
        CloseResult result = await Close(new FakeInstance { HasExited = true });

        Assert.Equal(CloseMethod.AlreadyExited, result.Method);
    }

    [Fact]
    public async Task Uses_the_webui_when_configured()
    {
        var instance = new FakeInstance();
        var handler = new FakeHttpHandler().On("http://localhost:8080/api/v2/app/shutdown", _ =>
        {
            instance.HasExited = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        CloseResult result = await Close(instance, webUi: () => new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), ApiKey: "k"), handler));

        Assert.Equal(CloseMethod.WebUi, result.Method);
        Assert.Equal(0, instance.RestartManagerCalls);
    }

    [Fact]
    public async Task Falls_back_to_restart_manager_when_the_webui_fails()
    {
        var instance = new FakeInstance();
        var handler = new FakeHttpHandler().OnStatus("http://localhost:8080/api/v2/app/shutdown", HttpStatusCode.Forbidden);

        CloseResult result = await Close(instance, webUi: () => new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), ApiKey: "bad"), handler));

        Assert.Equal(CloseMethod.RestartManager, result.Method);
        Assert.Equal(1, instance.RestartManagerCalls);
    }
}
