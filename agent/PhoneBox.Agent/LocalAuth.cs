using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// A per-install secret for the local port (127.0.0.1). The Host, Origin and Fetch Metadata checks in
/// <see cref="RequestGate"/> keep web pages out, but a process that is not a browser passes them. That covers another
/// OS user, a sandboxed app with network access, a container or a WSL distro on the host's loopback. Without this
/// secret such a process could read every token in the settings and run commands through the shortcut and command
/// endpoints.
/// <list type="bullet">
/// <item>The secret lives in <c>local-api.key</c> next to config.json, readable only by its owner. Deleting the file
/// and restarting the agent rotates it, which signs every browser out.</item>
/// <item>Scripts that read the key file send it in the <c>X-PhoneBox-Local</c> header.</item>
/// <item>A browser never holds the secret itself. A one-time link (time stamp, nonce, HMAC) gets it a session cookie: an
/// HMAC of the issue time that expires after <see cref="SessionLifetime"/>. Browsers send a 127.0.0.1 cookie to every
/// port, and a process that takes port 8765 while the agent is down could catch it. That process gets a session that
/// runs out, not the key. The tray, the menu bar and a second launch of the agent make the link, because they read the
/// key file as the same user.</item>
/// </list>
/// </summary>
public sealed class LocalAuth
{
    public const string FileName = "local-api.key";
    public const string CookieName = "phonebox_local";
    public const string HeaderName = "X-PhoneBox-Local";
    public const string LoginPath = "/login";
    internal static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    /// <summary>A clock that ticked between whoever made a link or cookie and the agent.</summary>
    private const long SkewSec = 5;

    private readonly byte[] _secret;
    private readonly ConcurrentDictionary<string, long> _usedLogins = new();

    public LocalAuth(string secret, bool ephemeral = false)
    {
        if (!IsWellFormed(secret)) throw new ArgumentException("malformed local API secret", nameof(secret));
        Secret = secret;
        Ephemeral = ephemeral;
        _secret = Encoding.ASCII.GetBytes(secret);
    }

    /// <summary>43 characters of unpadded base64url: 256 random bits.</summary>
    public string Secret { get; }

    /// <summary>True when the key file could not be read or written: this secret lasts until the agent stops.</summary>
    public bool Ephemeral { get; }

    /// <summary>
    /// Reads the secret next to the settings. Creates it (owner-only) when the file is missing or holds something else.
    /// A file that exists but cannot be read (an antivirus lock, an ACL) is left alone, and the agent runs with an
    /// in-memory secret instead of replacing the user's file or failing to start.
    /// </summary>
    public static LocalAuth LoadOrCreate(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        try
        {
            if (File.Exists(path) && IsWellFormed(File.ReadAllText(path).Trim()))
            {
                AppPaths.RestrictToOwner(path);
                return new LocalAuth(File.ReadAllText(path).Trim());
            }
            var secret = NewSecret();
            Directory.CreateDirectory(configDir);
            using (var file = AppPaths.CreatePrivate(path))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.Write(secret);
            return new LocalAuth(secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // another agent on the same settings may have just written it
            if (ReadSecret(configDir) is { } written) return new LocalAuth(written);
            return new LocalAuth(NewSecret(), ephemeral: true);
        }
    }

    /// <summary>The stored secret, or null when there is none (yet), it cannot be read, or it is not one this class wrote.</summary>
    public static string? ReadSecret(string configDir)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(configDir, FileName)).Trim();
            return IsWellFormed(text) ? text : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool IsWellFormed(string? s) =>
        s is { Length: 43 } && s.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>A one-time sign-in link for the settings page, made by whoever can read the key file.</summary>
    public static string LoginUrl(string settingsUrl, string secret, DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds();
        // a nonce, so two links made in the same second (a double click on the tray) are two links
        var n = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return $"{settingsUrl.TrimEnd('/')}{LoginPath}?t={t}&n={n}&sig={Mac(secret, $"phonebox-login:{t}:{n}")}";
    }

    /// <summary>The settings page's address, signed in when the key file can be read; the plain address otherwise.</summary>
    public static string SettingsUrlFor(string settingsUrl, string configDir) =>
        ReadSecret(configDir) is { } secret ? LoginUrl(settingsUrl, secret, DateTimeOffset.UtcNow) : settingsUrl;

    /// <summary>The settings page's address, signed in with this agent's own secret (the tray's way).</summary>
    public string LoginUrl(string settingsUrl) => LoginUrl(settingsUrl, Secret, DateTimeOffset.UtcNow);

    private static string Mac(string secret, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(message))).ToLowerInvariant();

    private bool SameMac(string message, string? candidate) =>
        candidate is { Length: 64 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(Secret, message)), Encoding.ASCII.GetBytes(candidate.ToLowerInvariant()));

    private static bool Fresh(long stamp, long nowSec, TimeSpan lifetime) =>
        stamp <= nowSec + SkewSec && nowSec - stamp <= (long)lifetime.TotalSeconds;

    /// <summary>True once for a fresh, correctly signed link; a replay or an expired link is refused.</summary>
    public bool TryLogin(string? t, string? n, string? sig, DateTimeOffset now)
    {
        if (!long.TryParse(t, System.Globalization.NumberStyles.None, null, out var stamp) || n is not { Length: 16 } || !n.All(char.IsAsciiHexDigit)) return false;
        var nowSec = now.ToUnixTimeSeconds();
        if (!Fresh(stamp, nowSec, LoginLifetime) || !SameMac($"phonebox-login:{stamp}:{n}", sig)) return false;
        foreach (var (key, at) in _usedLogins)
            if (nowSec - at > (long)LoginLifetime.TotalSeconds + SkewSec) _usedLogins.TryRemove(key, out _);
        return _usedLogins.TryAdd(sig!.ToLowerInvariant(), stamp);
    }

    /// <summary>A session cookie value issued now: "&lt;unix time&gt;.&lt;HMAC&gt;".</summary>
    public string NewSession(DateTimeOffset now)
    {
        var issued = now.ToUnixTimeSeconds();
        return $"{issued}.{Mac(Secret, $"phonebox-session:{issued}")}";
    }

    internal bool ValidSession(string? cookie, DateTimeOffset now)
    {
        if (cookie is null || cookie.IndexOf('.') is not (> 0 and var dot)) return false;
        if (!long.TryParse(cookie.AsSpan(0, dot), System.Globalization.NumberStyles.None, null, out var issued)) return false;
        return Fresh(issued, now.ToUnixTimeSeconds(), SessionLifetime) && SameMac($"phonebox-session:{issued}", cookie[(dot + 1)..]);
    }

    /// <summary>True when the request carries the secret in the header or a live session cookie.</summary>
    public bool Authorized(HttpRequest req) =>
        Matches(req.Headers[HeaderName].ToString()) || ValidSession(req.Cookies[CookieName], DateTimeOffset.UtcNow);

    private bool Matches(string? candidate) =>
        candidate is { Length: 43 } && CryptographicOperations.FixedTimeEquals(_secret, Encoding.ASCII.GetBytes(candidate));

    /// <summary>
    /// What the local port serves without the secret, by name: the settings page and the panel files (static, no
    /// data), the icon, the sign-in link, and GET /api/info, which installers poll to see that the agent is up (it
    /// answers them with the version only). Anything added later needs the secret unless it is listed here.
    /// </summary>
    public static bool IsOpen(HttpRequest req)
    {
        if (!HttpMethods.IsGet(req.Method)) return false;
        var path = req.Path;
        return path == "/"
            || path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/panel", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/info", StringComparison.OrdinalIgnoreCase);
    }

    public CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = SessionLifetime,
        IsEssential = true,
    };
}
