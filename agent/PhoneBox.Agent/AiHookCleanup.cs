namespace PhoneBox.Agent;

/// <summary>
/// With the AI integrations off (<see cref="Features.AiIntegrations"/>), removes the permission-prompt hooks this agent
/// installed earlier. A hook left behind keeps posting every Bash and Write request to 127.0.0.1. While the agent is
/// down, any local process that takes the port could read those requests or answer "allow" to them.
/// Only a file that holds PhoneBox's own hook is rewritten, and a symlinked settings file is written through to its
/// target, so a link into a dotfiles repository stays a link. The rewrite keeps the file's other hooks and saves a
/// backup first (<c>.phonebox.bak</c>), the same as the settings page's Remove button.
/// </summary>
public static class AiHookCleanup
{
    public static void RemoveInstalled(int port, AgentConfig cfg, ILogger log)
    {
        if (Features.AiIntegrations) return;
        // Claude Code: ~/.claude and every extra account folder, each on its own
        foreach (var target in Safe(() => ClaudeHook.Targets(cfg.Claude.ExtraCredentialDirs), log) ?? [])
            if (target.Installed)
                Remove("Claude Code", target.Path, p => ClaudeHook.Configure(false, port, cfg.Claude.TimeoutSec, p, ClaudeHook.Endpoint), log);
        if (CodexHook.Installed())
            Remove("Codex", CodexHook.HooksPath, p => ClaudeHook.Configure(false, port, cfg.Claude.TimeoutSec, p, CodexHook.Endpoint), log);
        if (WorkBuddyHook.Installed())
            Remove("WorkBuddy", WorkBuddyHook.SettingsPath, p => ClaudeHook.Configure(false, port, cfg.WorkBuddy.TimeoutSec, p, WorkBuddyHook.Endpoint), log);
        // the native ~/.cursor/hooks.json only: distros are not started for this (Remove on a running one does that)
        if (Holds(CursorHook.HooksPath, CursorHook.Endpoint))
            Remove("Cursor", CursorHook.HooksPath, _ => CursorHook.ConfigureAll(false, port, cfg.Claude.TimeoutSec, wsl: false), log);
    }

    private static bool Holds(string path, string endpoint)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(endpoint, StringComparison.Ordinal); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The file a settings path really is: a symlink's final target, so replacing it keeps the link.</summary>
    internal static string RealPath(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }

    private static T? Safe<T>(Func<T> read, ILogger log) where T : class
    {
        try { return read(); }
        catch (Exception ex) { log.LogWarning("AI integrations are off; listing the AI tools' settings failed: {Error}", ex.Message); return null; }
    }

    private static void Remove(string tool, string path, Func<string, string?> remove, ILogger log)
    {
        try
        {
            var real = RealPath(path);
            if (remove(real) is { } error) log.LogWarning("AI integrations are off, but the {Tool} hook in {Path} could not be removed: {Error}", tool, real, error);
            else log.LogInformation("AI integrations are off: removed the {Tool} permission hook from {Path} (backup: {Backup})", tool, real, real + ".phonebox.bak");
        }
        catch (Exception ex) { log.LogWarning("AI integrations are off; removing the {Tool} hook failed: {Error}", tool, ex.Message); }
    }
}
