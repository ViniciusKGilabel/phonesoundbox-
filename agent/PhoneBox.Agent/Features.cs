namespace PhoneBox.Agent;

/// <summary>
/// Switches for whole feature areas in this fork. The code behind a switched-off area stays in the tree so changes from
/// the upstream project still merge cleanly. The agent just does not start, map or show it.
/// </summary>
public static class Features
{
    /// <summary>
    /// The AI coding integrations, plus GitHub, which shares their widgets and templates. When this is off:
    /// <list type="bullet">
    /// <item>no quota polling (no AI vendor token is read), no session or transcript reading, no GitHub polling;</item>
    /// <item>no permission-prompt hooks or approvals, and PhoneBox's own hooks are removed from the AI tools' settings;</item>
    /// <item>no GitHub workflow runs;</item>
    /// <item>their widgets, templates and settings sections are hidden.</item>
    /// </list>
    /// It is a static readonly field, not a const, so the compiler does not warn about unreachable code when it is off.
    /// Turning it back on also needs the hooks to authenticate: the local API now requires <see cref="LocalAuth"/>'s
    /// secret, and the hook commands (ClaudeHook.HookCommand, CursorHook, docs/copilot-cli-bridge.py) do not send it.
    /// </summary>
    public static readonly bool AiIntegrations = false;

    /// <summary>Widget types that belong to the AI integrations (and GitHub).</summary>
    public static readonly IReadOnlySet<string> AiWidgetTypes =
        new HashSet<string>(StringComparer.Ordinal) { "quotas", "codexbar", "quotaring", "sessions", "github", "workflow-runner" };

    private static readonly string[] AiPaths =
        ["/api/hooks", "/api/claude", "/api/codex", "/api/cursor", "/api/workbuddy", "/api/approvals", "/api/quotas", "/api/work"];

    /// <summary>
    /// True for an API path of a switched-off area. <see cref="RequestGate"/> answers 404 for these on every port, so
    /// the endpoints in Program.cs can stay as upstream wrote them.
    /// </summary>
    public static bool IsDisabledPath(PathString path) =>
        !AiIntegrations && AiPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the editor may offer a widget of this type.</summary>
    public static bool WidgetAllowed(string? type) => AiIntegrations || type is null || !AiWidgetTypes.Contains(type);
}
