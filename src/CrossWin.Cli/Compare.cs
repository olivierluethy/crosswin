using System.Text.Json.Nodes;

namespace CrossWin;

/// <summary>
/// Structural comparison of two UI snapshots in the shared node schema (WPF/WinForms, Android and web all emit
/// <c>{framework, nodes:[...]}</c> from <c>ui --json</c>). Given an authorized reference capture and the current
/// implementation it returns a compact, backend-agnostic diff: elements present in the reference but missing from the
/// implementation, extra elements, and matched elements whose value, toggle, enabled state or geometry changed. This
/// is a <b>structural / behavioural</b> comparison (the element tree), not pixel similarity — two screens that look
/// alike can still differ here, and vice versa (issue #6 reference-comparison).
/// </summary>
public static class Compare
{
    private const int MaxList = 60;

    public static JsonObject Diff(JsonObject reference, JsonObject implementation, bool geometry, int tol)
    {
        var refNodes = Nodes(reference);
        var implNodes = Nodes(implementation);
        bool[] used = new bool[implNodes.Count];

        var missing = new JsonArray();
        var changed = new JsonArray();
        int matched = 0;

        foreach (var r in refNodes)
        {
            int j = FindMatch(r, implNodes, used);
            if (j < 0) { missing.Add(Label(r)); continue; }
            used[j] = true;
            matched++;
            var diffs = FieldDiffs(r, implNodes[j], geometry, tol);
            if (diffs.Count > 0)
            {
                var entry = Label(r);
                entry["diffs"] = diffs;
                changed.Add(entry);
            }
        }

        var extra = new JsonArray();
        for (int i = 0; i < implNodes.Count; i++)
            if (!used[i]) extra.Add(Label(implNodes[i]));

        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["reference"] = refNodes.Count,
                ["implementation"] = implNodes.Count,
                ["matched"] = matched,
                ["missing"] = missing.Count,
                ["extra"] = extra.Count,
                ["changed"] = changed.Count,
                ["geometry"] = geometry,
            },
            ["missing"] = Cap(missing),
            ["extra"] = Cap(extra),
            ["changed"] = Cap(changed),
        };
    }

    // Greedy matching: exact (role+name+id), then role+name, then role+id — each impl node used once.
    private static int FindMatch(JsonObject r, List<JsonObject> impl, bool[] used)
    {
        string role = Role(r), name = Name(r), id = Id(r);
        foreach (Func<JsonObject, bool> pred in new Func<JsonObject, bool>[]
        {
            n => Role(n) == role && Name(n) == name && Id(n) == id,
            n => Role(n) == role && Name(n) == name && name.Length > 0,
            n => Role(n) == role && Id(n) == id && id.Length > 0,
        })
            for (int i = 0; i < impl.Count; i++)
                if (!used[i] && pred(impl[i])) return i;
        return -1;
    }

    private static JsonArray FieldDiffs(JsonObject r, JsonObject impl, bool geometry, int tol)
    {
        var diffs = new JsonArray();
        void Field(string field, string? a, string? b) { if ((a ?? "") != (b ?? "")) diffs.Add(new JsonObject { ["field"] = field, ["reference"] = a, ["implementation"] = b }); }
        Field("value", r["value"]?.ToString(), impl["value"]?.ToString());
        Field("toggle", r["toggle"]?.ToString(), impl["toggle"]?.ToString());
        // "enabled":false present = disabled; absent = enabled.
        Field("enabled", (r["enabled"] == null).ToString().ToLowerInvariant(), (impl["enabled"] == null).ToString().ToLowerInvariant());
        if (geometry)
        {
            int[] a = Box(r), b = Box(impl);
            if (Enumerable.Range(0, 4).Any(k => Math.Abs(a[k] - b[k]) > tol))
                diffs.Add(new JsonObject { ["field"] = "bounds", ["reference"] = $"[{a[0]},{a[1]} {a[2]}x{a[3]}]", ["implementation"] = $"[{b[0]},{b[1]} {b[2]}x{b[3]}]" });
        }
        return diffs;
    }

    // ------------------------------------------------------------------ CLI

    public static int Run(string[] args)
    {
        var files = new List<string>();
        bool geometry = false, json = false;
        int tol = 6;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--json") { json = true; }
            else if (args[i] == "--geometry") { geometry = true; if (i + 1 < args.Length && int.TryParse(args[i + 1], out int t)) { tol = t; i++; } }
            else if (args[i].StartsWith("--")) { /* ignore unknown flag */ }
            else files.Add(args[i]);
        }
        if (files.Count < 2)
            throw new CrossWinException("Usage: crosswin compare <reference.json> <current.json> [--geometry [px]] [--json]\n  Capture each with: crosswin ui --json  (or  crosswin web ui --json / crosswin mobile ui --json).");

        var diff = Diff(LoadSnapshot(files[0]), LoadSnapshot(files[1]), geometry, tol);
        if (json) Console.WriteLine(diff.ToJsonString(SessionLog.Json));
        else PrintText(diff);

        var s = diff["summary"]!.AsObject();
        int issues = s["missing"]!.GetValue<int>() + s["extra"]!.GetValue<int>() + s["changed"]!.GetValue<int>();
        return issues == 0 ? 0 : 2; // non-zero so it is usable as an assertion
    }

    public static void PrintText(JsonObject diff)
    {
        var s = diff["summary"]!.AsObject();
        Console.WriteLine($"compare: reference {s["reference"]} nodes, implementation {s["implementation"]} — matched {s["matched"]}, missing {s["missing"]}, extra {s["extra"]}, changed {s["changed"]}");
        Section("missing (in reference, not found in implementation)", diff["missing"]!.AsArray(), o => "  - " + Describe(o));
        Section("extra (only in implementation)", diff["extra"]!.AsArray(), o => "  + " + Describe(o));
        Section("changed", diff["changed"]!.AsArray(), o =>
        {
            string ds = string.Join(" ; ", o["diffs"]!.AsArray().Select(d => $"{d!["field"]} \"{d["reference"]}\" → \"{d["implementation"]}\""));
            return $"  ~ {Describe(o)}: {ds}";
        });
        if (s["missing"]!.GetValue<int>() + s["extra"]!.GetValue<int>() + s["changed"]!.GetValue<int>() == 0)
            Console.WriteLine("No structural differences.");
    }

    private static void Section(string title, JsonArray items, Func<JsonObject, string> line)
    {
        if (items.Count == 0) return;
        Console.WriteLine(title + ":");
        foreach (var n in items)
        {
            var o = n!.AsObject();
            if (o["_more"] is { } m) { Console.WriteLine($"    … and {m} more"); continue; }
            Console.WriteLine(line(o));
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Accepts a file path or an inline JSON string; normalises to a UI-snapshot object.</summary>
    public static JsonObject LoadSnapshot(string pathOrJson)
    {
        string text = File.Exists(pathOrJson) ? File.ReadAllText(pathOrJson) : pathOrJson;
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch (System.Text.Json.JsonException ex) { throw new CrossWinException($"Could not read a UI snapshot from '{Short(pathOrJson)}': {ex.Message}"); }
        return node switch
        {
            JsonArray arr => new JsonObject { ["nodes"] = arr.DeepClone() },
            JsonObject obj when obj["nodes"] is JsonArray => obj,
            _ => throw new CrossWinException($"'{Short(pathOrJson)}' is not a UI snapshot (expected {{\"nodes\":[…]}} or a nodes array from  ui --json)."),
        };
    }

    private static List<JsonObject> Nodes(JsonObject snapshot) =>
        (snapshot["nodes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

    private static JsonArray Cap(JsonArray a)
    {
        if (a.Count <= MaxList) return a;
        int more = a.Count - MaxList;
        var trimmed = new JsonArray(a.Take(MaxList).Select(x => x!.DeepClone()).ToArray());
        trimmed.Add(new JsonObject { ["_more"] = more });
        return trimmed;
    }

    private static JsonObject Label(JsonObject n)
    {
        var o = new JsonObject { ["role"] = Role(n), ["name"] = n["name"]?.ToString() ?? "" };
        if (n["id"] is { } id) o["id"] = id.ToString();
        return o;
    }

    private static string Describe(JsonObject n)
    {
        string label = n["role"]?.ToString() ?? "";
        if (n["name"]?.ToString() is { Length: > 0 } nm) label += $" \"{nm}\"";
        if (n["id"]?.ToString() is { Length: > 0 } id) label += $" #{id}";
        return label;
    }

    private static string Role(JsonObject n) => n["role"]?.ToString() ?? "";
    private static string Name(JsonObject n) => (n["name"]?.ToString() ?? "").Trim().ToLowerInvariant().Replace('\n', ' ');
    private static string Id(JsonObject n) => n["id"]?.ToString() ?? "";

    private static int[] Box(JsonObject n)
    {
        int I(string k) => n[k] is { } v && int.TryParse(v.ToString(), out int x) ? x : 0;
        return new[] { I("x"), I("y"), I("w"), I("h") };
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
