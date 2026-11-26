using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The local port's per-install secret (LocalAuth) and how RequestGate applies it. Before it existed, any process that
/// is not a browser (another OS user, a sandboxed app, a container on the host loopback) could read every token in the
/// settings and run commands through the local API.
/// </summary>
[Collection("Approval request expiry")] // the phone-header cases read the shared PhoneLastSeen and AppVersion
public class LocalAuthTests : IDisposable
{
    private const int Local = 8765;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-local-auth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void TheSecretIsCreatedOwnerOnlyAndKeptAcrossRestarts()
    {
        var first = LocalAuth.LoadOrCreate(_dir);
        var second = LocalAuth.LoadOrCreate(_dir);
        Assert.Equal(first.Secret, second.Secret);
        Assert.True(LocalAuth.IsWellFormed(first.Secret));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, LocalAuth.FileName)));
    }

    [Fact]
    public void ADamagedKeyFileIsReplaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, LocalAuth.FileName), "not a key");
        Assert.Null(LocalAuth.ReadSecret(_dir));
        var auth = LocalAuth.LoadOrCreate(_dir);
        Assert.Equal(auth.Secret, LocalAuth.ReadSecret(_dir));
    }

    [Fact]
    public void ASignInLinkWorksOnceAndOnlyWhileFresh()
    {
        var auth = new LocalAuth(LocalAuth.NewSecret());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var (t, n, sig) = Parse(LocalAuth.LoginUrl("http://127.0.0.1:8765/", auth.Secret, now));

        Assert.True(auth.TryLogin(t, n, sig, now.AddSeconds(10)));
        Assert.False(auth.TryLogin(t, n, sig, now.AddSeconds(11))); // replayed (browser history, a shared screenshot)

        // a second link in the same second (a double click on the tray) is a different link
        var (t2, n2, sig2) = Parse(LocalAuth.LoginUrl("http://127.0.0.1:8765/", auth.Secret, now));
        Assert.True(auth.TryLogin(t2, n2, sig2, now.AddSeconds(12)));

        var (t3, n3, sig3) = Parse(LocalAuth.LoginUrl("http://127.0.0.1:8765/", auth.Secret, now));
        Assert.False(auth.TryLogin(t3, n3, sig3, now + LocalAuth.LoginLifetime + TimeSpan.FromSeconds(1))); // expired
    }

    [Fact]
    public void ASignInLinkFromAnotherSecretOrATamperedStampIsRefused()
    {
        var auth = new LocalAuth(LocalAuth.NewSecret());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var (t, n, sig) = Parse(LocalAuth.LoginUrl("http://127.0.0.1:8765/", LocalAuth.NewSecret(), now));
        Assert.False(auth.TryLogin(t, n, sig, now));

        var (t2, n2, sig2) = Parse(LocalAuth.LoginUrl("http://127.0.0.1:8765/", auth.Secret, now));
        Assert.False(auth.TryLogin((long.Parse(t2) + 1).ToString(), n2, sig2, now));
        Assert.False(auth.TryLogin(t2, "0000000000000000", sig2, now));
        Assert.False(auth.TryLogin(t2, n2, null, now));
        Assert.False(auth.TryLogin("+" + t2, n2, sig2, now));
        Assert.False(auth.TryLogin("soon", n2, sig2, now));
    }

    [Fact]
    public void TheCookieIsASessionThatRunsOutNeverTheKey()
    {
        var auth = new LocalAuth(LocalAuth.NewSecret());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var session = auth.NewSession(now);
        Assert.DoesNotContain(auth.Secret, session);
        Assert.True(auth.ValidSession(session, now.AddDays(1)));
        Assert.False(auth.ValidSession(session, now + LocalAuth.SessionLifetime + TimeSpan.FromSeconds(1)));
        Assert.False(auth.ValidSession(new LocalAuth(LocalAuth.NewSecret()).NewSession(now), now)); // another install's
        Assert.False(auth.ValidSession(auth.Secret, now)); // the raw key is not a session
        Assert.False(auth.ValidSession((now.ToUnixTimeSeconds() + 1) + session[session.IndexOf('.')..], now));
    }

    [Theory]
    [InlineData("GET", "/api/settings", 401)]
    [InlineData("PUT", "/api/settings", 401)]
    [InlineData("POST", "/api/shortcuts/test", 401)]
    [InlineData("GET", "/api/approvals", 404)] // switched off with the AI integrations, on every port
    [InlineData("POST", "/api/hooks/claude/permission", 404)]
    [InlineData("GET", "/api/quotas", 404)]
    [InlineData("GET", "/api/info", 200)] // installers poll it; it answers them with the version only
    [InlineData("GET", "/", 200)]
    [InlineData("GET", "/login", 200)]
    [InlineData("GET", "/panel/index.html", 200)]
    [InlineData("GET", "/favicon.ico", 200)]
    [InlineData("POST", "/", 401)] // open paths are listed by name and method
    [InlineData("GET", "/something-new", 401)]
    public async Task WithoutTheSecretTheLocalPortServesOnlyThePageAndTheVersion(string method, string path, int expected)
    {
        var gate = Gate(new LocalAuth(LocalAuth.NewSecret()));
        Assert.Equal(expected, await Status(gate, Request(method, path)));
    }

    [Fact]
    public async Task TheSecretInTheHeaderOrTheCookieOpensTheApi()
    {
        var auth = new LocalAuth(LocalAuth.NewSecret());
        var gate = Gate(auth);

        var header = Request("GET", "/api/settings");
        header.Request.Headers[LocalAuth.HeaderName] = auth.Secret;
        Assert.Equal(200, await Status(gate, header));

        var cookie = Request("PUT", "/api/settings");
        cookie.Request.Headers.Cookie = $"{LocalAuth.CookieName}={auth.NewSession(DateTimeOffset.UtcNow)}";
        Assert.Equal(200, await Status(gate, cookie));

        var rawKeyCookie = Request("GET", "/api/settings");
        rawKeyCookie.Request.Headers.Cookie = $"{LocalAuth.CookieName}={auth.Secret}";
        Assert.Equal(401, await Status(gate, rawKeyCookie));

        var wrong = Request("GET", "/api/settings");
        wrong.Request.Headers[LocalAuth.HeaderName] = LocalAuth.NewSecret();
        Assert.Equal(401, await Status(gate, wrong));
    }

    [Fact]
    public async Task ForgedBrowserHeadersDoNotOpenTheApi()
    {
        // what the old Sec-Fetch-based redaction of GET /api/settings trusted
        var gate = Gate(new LocalAuth(LocalAuth.NewSecret()));
        var forged = Request("GET", "/api/settings");
        forged.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        forged.Request.Headers["Sec-Fetch-Mode"] = "cors";
        forged.Request.Headers["Sec-Fetch-Dest"] = "empty";
        Assert.Equal(401, await Status(gate, forged));
    }

    [Fact]
    public async Task PhoneHeadersWithoutTheSecretChangeNothing()
    {
        // an open path reached without the secret must not fake a connected phone or an app version
        var gate = Gate(new LocalAuth(LocalAuth.NewSecret()));
        var before = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
        var version = UpdateService.AppVersion;
        var fake = Request("GET", "/panel/index.html");
        fake.Request.Headers["X-PhoneBox-App"] = "0.0.1-fake";
        fake.Request.Headers["X-PhoneBox-Device"] = "fake-device";
        Assert.Equal(200, await Status(gate, fake));
        Assert.Equal(before, Interlocked.Read(ref ApprovalStore.PhoneLastSeen));
        Assert.Equal(version, UpdateService.AppVersion);
    }

    [Fact]
    public void ASymlinkedSettingsFileIsWrittenThroughToItsTarget()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need a privilege there
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "dotfiles-settings.json");
        File.WriteAllText(target, "{}");
        var link = Path.Combine(_dir, "settings.json");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(Path.GetFullPath(target), AiHookCleanup.RealPath(link));
        Assert.Equal(target, AiHookCleanup.RealPath(target));
    }

    [Fact]
    public void TheEditorOffersNoAiWidgetsOrTemplates()
    {
        Assert.False(Features.AiIntegrations);
        foreach (var type in Features.AiWidgetTypes)
        {
            Assert.DoesNotContain($"\"type\":\"{type}\"", Catalog.OfferedWidgetsJson);
            Assert.DoesNotContain($"\"type\":\"{type}\"", Catalog.OfferedTemplatesJson);
        }
        Assert.Contains("\"type\":\"clock\"", Catalog.OfferedWidgetsJson);
        Assert.DoesNotContain("ai", ConfigStore.DefaultPages);
        Assert.DoesNotContain("work", ConfigStore.DefaultPages);
    }

    [Fact]
    public void UpdatesComeFromThisForkNotUpstream()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "updates": { "repo": "ersinkoc/phonebox" } }""");
        Assert.Equal(UpdateConfig.DefaultRepo, new ConfigStore(path).Get().Updates.Repo);
    }

    private static RequestGate Gate(LocalAuth auth)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-local-auth-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new ConfigStore(Path.Combine(dir, "config.json"));
        return new RequestGate(Local, [$"http://127.0.0.1:{Local}"], new UsbLink(8767, "unused"), null, cfg, _ => false,
            NullLogger.Instance, auth);
    }

    private static DefaultHttpContext Request(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString("127.0.0.1", Local);
        ctx.Connection.LocalPort = Local;
        return ctx;
    }

    private static async Task<int> Status(RequestGate gate, DefaultHttpContext ctx)
    {
        var reached = false;
        await gate.InvokeAsync(ctx, () => { reached = true; return Task.CompletedTask; });
        return reached ? 200 : ctx.Response.StatusCode;
    }

    private static (string T, string N, string Sig) Parse(string url)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query);
        return (query["t"].ToString(), query["n"].ToString(), query["sig"].ToString());
    }
}
