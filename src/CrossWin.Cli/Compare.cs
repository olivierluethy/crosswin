using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
            throw new CrossWinException("Usage: crosswin compare <reference> <current> [--geometry [px]] [--json]\n  UI snapshots from  ui --json , or event traces from  events --json  (auto-detected).");

        // Auto-detect: event traces have "kind"; UI snapshots have "nodes"/"role".
        bool events = FirstObject(files[0]) is { } fo && fo["kind"] != null;
        if (events)
        {
            var ediff = DiffEvents(LoadEvents(files[0]), LoadEvents(files[1]));
            if (json) Console.WriteLine(ediff.ToJsonString(SessionLog.Json));
            else PrintEventsText(ediff);
            return ediff["summary"]!["regressions"]!.GetValue<int>() == 0 ? 0 : 2;
        }

        var diff = Diff(LoadSnapshot(files[0]), LoadSnapshot(files[1]), geometry, tol);
        if (json) Console.WriteLine(diff.ToJsonString(SessionLog.Json));
        else PrintText(diff);

        var s = diff["summary"]!.AsObject();
        int issues = s["missing"]!.GetValue<int>() + s["extra"]!.GetValue<int>() + s["changed"]!.GetValue<int>();
        return issues == 0 ? 0 : 2; // non-zero so it is usable as an assertion
    }

    // ------------------------------------------------------------------ behavioural (event-trace) comparison

    /// <summary>
    /// Compares two session event traces (from <c>events --json</c>): which errors/warnings the implementation
    /// resolved versus the reference, which are new (regressions) and which persist, plus whether the recorded
    /// interaction sequence matches. Error identity ignores volatile numbers/addresses so the same failure groups.
    /// </summary>
    public static JsonObject DiffEvents(List<JsonObject> reference, List<JsonObject> implementation)
    {
        var refErr = Problems(reference);
        var implErr = Problems(implementation);

        JsonArray List(IEnumerable<string> keys, Dictionary<string, JsonObject> from) =>
            Cap(new JsonArray(keys.OrderBy(k => k).Select(k => (JsonNode?)from[k].DeepClone()).ToArray()));

        var resolved = refErr.Keys.Where(k => !implErr.ContainsKey(k)).ToList();
        var regressions = implErr.Keys.Where(k => !refErr.ContainsKey(k)).ToList();
        var persisting = refErr.Keys.Where(implErr.ContainsKey).ToList();

        var refActs = Actions(reference);
        var implActs = Actions(implementation);

        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["referenceProblems"] = refErr.Count,
                ["implementationProblems"] = implErr.Count,
                ["resolved"] = resolved.Count,
                ["regressions"] = regressions.Count,
                ["persisting"] = persisting.Count,
                ["interactionsMatch"] = refActs.SequenceEqual(implActs),
                ["referenceInteractions"] = refActs.Count,
                ["implementationInteractions"] = implActs.Count,
            },
            ["resolved"] = List(resolved, refErr),
            ["regressions"] = List(regressions, implErr),
            ["persisting"] = List(persisting, implErr),
        };
    }

    public static void PrintEventsText(JsonObject diff)
    {
        var s = diff["summary"]!.AsObject();
        Console.WriteLine($"compare (behaviour): reference {s["referenceProblems"]} problem(s), implementation {s["implementationProblems"]} — resolved {s["resolved"]}, regressions {s["regressions"]}, persisting {s["persisting"]}");
        Console.WriteLine($"  interactions: reference {s["referenceInteractions"]}, implementation {s["implementationInteractions"]}, sequence matches: {s["interactionsMatch"]}");
        Section("regressions (new in implementation)", diff["regressions"]!.AsArray(), ProblemLine("+ "));
        Section("resolved (gone from implementation)", diff["resolved"]!.AsArray(), ProblemLine("- "));
        Section("persisting (in both)", diff["persisting"]!.AsArray(), ProblemLine("~ "));
        if (s["regressions"]!.GetValue<int>() == 0 && s["resolved"]!.GetValue<int>() == 0 && s["persisting"]!.GetValue<int>() == 0)
            Console.WriteLine("No problems in either trace.");
    }

    private static Func<JsonObject, string> ProblemLine(string prefix) => o =>
    {
        string loc = o["file"] is { } f ? $"  {f}:{o["line"]}" : "";
        string? tc = o["type"]?.ToString() ?? o["code"]?.ToString();
        return $"  {prefix}[{o["level"]}] {o["kind"]}  {(tc is { Length: > 0 } ? tc + ": " : "")}{o["msg"]}{loc}";
    };

    // Error/warning events keyed by a signature that ignores volatile numbers so the same failure groups.
    private static Dictionary<string, JsonObject> Problems(List<JsonObject> events)
    {
        var map = new Dictionary<string, JsonObject>();
        foreach (var e in events)
        {
            string level = e["level"]?.ToString() ?? "";
            if (level is not ("error" or "warn")) continue;
            string kind = e["kind"]?.ToString() ?? "";
            string tc = e["type"]?.ToString() ?? e["code"]?.ToString() ?? "";
            string msg = Regex.Replace((e["msg"]?.ToString() ?? "").ToLowerInvariant(), @"0x[0-9a-f]+|\d+", "#").Trim();
            string file = e["file"]?.ToString() ?? "";
            string key = $"{kind}|{tc}|{msg}|{file}";
            if (!map.ContainsKey(key))
                map[key] = new JsonObject
                {
                    ["kind"] = kind, ["level"] = level, ["msg"] = e["msg"]?.ToString() ?? "",
                    ["type"] = e["type"]?.ToString(), ["code"] = e["code"]?.ToString(),
                    ["file"] = e["file"]?.ToString(), ["line"] = e["line"]?.DeepClone(),
                };
        }
        return map;
    }

    private static List<string> Actions(List<JsonObject> events) =>
        events.Where(e => e["kind"]?.ToString() == "interaction.batch")
            .SelectMany(e => (e["actions"] as JsonArray)?.Select(x => x!.ToString()) ?? Enumerable.Empty<string>())
            .ToList();

    private static List<JsonObject> LoadEvents(string pathOrJson)
    {
        string text = File.Exists(pathOrJson) ? File.ReadAllText(pathOrJson) : pathOrJson;
        var list = new List<JsonObject>();
        try { if (JsonNode.Parse(text) is JsonArray arr) { list.AddRange(arr.OfType<JsonObject>()); return list; } } catch (System.Text.Json.JsonException) { }
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            try { if (JsonNode.Parse(t) is JsonObject o) list.Add(o); } catch (System.Text.Json.JsonException) { }
        }
        return list;
    }

    private static JsonObject? FirstObject(string pathOrJson)
    {
        string text = File.Exists(pathOrJson) ? File.ReadAllText(pathOrJson) : pathOrJson;
        try
        {
            var n = JsonNode.Parse(text);
            if (n is JsonObject o) return o;
            if (n is JsonArray a) return a.OfType<JsonObject>().FirstOrDefault();
        }
        catch (System.Text.Json.JsonException) { }
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            try { return JsonNode.Parse(t) as JsonObject; } catch (System.Text.Json.JsonException) { }
        }
        return null;
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
