using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A page template: a ready-made widget layout the settings UI (and migration) can start from.</summary>
public sealed class TemplateDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Desc { get; set; } = "";
    public List<WidgetConfig> Widgets { get; set; } = [];
}

/// <summary>The widget types and page templates shipped with the agent (widgets.json, templates.json).</summary>
public static class Catalog
{
    public const int Cols = 12;
    /// <summary>Rows on a phone. A tablet shows more (see the panel's grid.js); <see cref="MaxRows"/> is the limit.</summary>
    public const int Rows = 6;
    /// <summary>Tallest page that can be saved. Widgets below row <see cref="Rows"/> show on a tablet and stay off a phone.</summary>
    public const int MaxRows = 18;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static readonly string WidgetsJson = Read("widgets.json");
    public static readonly string TemplatesJson = Read("templates.json");

    public static readonly IReadOnlyList<TemplateDef> Templates =
        JsonSerializer.Deserialize<List<TemplateDef>>(TemplatesJson, Opts) ?? [];

    /// <summary>widgets.json without the widgets of switched-off areas (<see cref="Features"/>): what the editor offers.</summary>
    public static readonly string OfferedWidgetsJson = OfferedWidgets(WidgetsJson);

    /// <summary>templates.json without those widgets; a template left with none is not offered at all.</summary>
    public static readonly string OfferedTemplatesJson = OfferedTemplates(TemplatesJson);

    internal static string OfferedWidgets(string json)
    {
        if (Features.AiIntegrations) return json;
        var list = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray();
        foreach (var w in list.Where(w => !Features.WidgetAllowed((string?)w?["type"])).ToList()) list.Remove(w);
        return list.ToJsonString();
    }

    internal static string OfferedTemplates(string json)
    {
        if (Features.AiIntegrations) return json;
        var list = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray();
        foreach (var t in list.ToList())
        {
            if (t?["widgets"] is not System.Text.Json.Nodes.JsonArray widgets) continue;
            foreach (var w in widgets.Where(w => !Features.WidgetAllowed((string?)w?["type"])).ToList()) widgets.Remove(w);
            if (widgets.Count == 0) list.Remove(t);
        }
        return list.ToJsonString();
    }

    private static readonly Dictionary<string, (int MinW, int MinH)> Sizes = LoadSizes();

    public static bool IsWidget(string type) => Sizes.ContainsKey(type);

    public static (int MinW, int MinH) MinSize(string type) => Sizes.TryGetValue(type, out var s) ? s : (1, 1);

    public static TemplateDef? Template(string? id) => id is null ? null : Templates.FirstOrDefault(t => t.Id == id);

    private static Dictionary<string, (int, int)> LoadSizes()
    {
        using var doc = JsonDocument.Parse(WidgetsJson);
        return doc.RootElement.EnumerateArray().ToDictionary(
            w => w.GetProperty("type").GetString()!,
            w => (w.GetProperty("minW").GetInt32(), w.GetProperty("minH").GetInt32()));
    }

    private static string Read(string name)
    {
        using var s = AppPaths.Resource(name) ?? throw new InvalidOperationException($"missing resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
