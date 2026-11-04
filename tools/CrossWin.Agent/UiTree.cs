// Semantic UI tree of the running app, read in-process from the framework's own accessibility layer:
//  - WPF:      AutomationPeers (the objects UI Automation would expose on Windows)
//  - WinForms: Control tree + AccessibilityObject (MSAA role/name)
// Uses reflection/dynamic only, so the agent keeps no compile-time dependency on WPF/WinForms.
// Coordinates are pixels in the client area of the main window (same space as "click X Y" and snap PNGs).
// Privacy: password fields are reported as "<redacted>", values are truncated.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;

internal sealed class UiNode
{
    public int Depth { get; set; }
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Id { get; set; }
    public string? Value { get; set; }
    public string? Toggle { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Focused { get; set; }
    public bool Offscreen { get; set; }
    public string? Window { get; set; }

    public int CenterX => X + W / 2;
    public int CenterY => Y + H / 2;
}

internal static class UiTree
{
    private const int MaxNodes = 1500, MaxValue = 200;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Dump(string _)
    {
        var (nodes, framework) = Read();
        return JsonSerializer.Serialize(new { framework, nodes = nodes.Select(n => new
        {
            n.Depth, n.Role, n.Name, n.Id, n.Value, n.Toggle, n.X, n.Y, n.W, n.H,
            enabled = n.Enabled ? (bool?)null : false, focused = n.Focused ? true : (bool?)null,
            offscreen = n.Offscreen ? true : (bool?)null, n.Window,
        }) }, Json);
    }

    public static (List<UiNode> Nodes, string Framework) Read()
    {
        var (_, left, top) = Snap.MainWindow();
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        if (asms.Any(a => a.GetName().Name == "PresentationFramework") && WpfApp() != null)
            return (Wpf.Read(left, top), "wpf");
        if (asms.Any(a => a.GetName().Name == "System.Windows.Forms"))
            return (WinForms.Read(left, top), "winforms");
        return (new List<UiNode>(), "unknown");
    }

    internal static object? WpfApp()
    {
        var t = Type.GetType("System.Windows.Application, PresentationFramework");
        return t?.GetProperty("Current")?.GetValue(null);
    }

    /// <summary>Best element for a visible label: exact name, then prefix, then substring; prefers
    /// enabled, on-screen, interactive elements over plain text.</summary>
    public static UiNode? Find(List<UiNode> nodes, string text)
    {
        string t = text.Trim();
        int Score(UiNode n)
        {
            string name = n.Name.Trim(), id = n.Id ?? "";
            int s = name.Equals(t, StringComparison.OrdinalIgnoreCase) || id.Equals(t, StringComparison.OrdinalIgnoreCase)
                    || (n.Value?.Equals(t, StringComparison.OrdinalIgnoreCase) ?? false) ? 300
                : name.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 200
                : name.Contains(t, StringComparison.OrdinalIgnoreCase) || (n.Value?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ? 100 : 0;
            if (s == 0 || n.W <= 0 || n.H <= 0) return 0;
            if (n.Enabled) s += 20;
            if (!n.Offscreen) s += 20;
            if (Interactive.Contains(n.Role)) s += 10;
            return s;
        }
        return nodes.Select(n => (n, s: Score(n))).Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => x.n).FirstOrDefault();
    }

    private static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "CheckBox", "RadioButton", "MenuItem", "ListItem", "TabItem", "TreeItem", "Hyperlink", "ComboBox",
        "Edit", "PushButton", "CheckButton", "Link", "PageTab", "OutlineItem",
    };

    internal static string Clip(string? s) => s == null ? "" : s.Length > MaxValue ? s[..MaxValue] + "…" : s;

    // ------------------------------------------------------------------------------------------- WPF
    private static class Wpf
    {
        public static List<UiNode> Read(int left, int top)
        {
            dynamic app = WpfApp()!;
            object dispatcher = app.Dispatcher;
            var result = new List<UiNode>();
            Func<object?> work = () =>
            {
                var peerType = Type.GetType("System.Windows.Automation.Peers.UIElementAutomationPeer, PresentationCore")!;
                var create = peerType.GetMethod("CreatePeerForElement", BindingFlags.Public | BindingFlags.Static)!;
                foreach (object window in (IEnumerable)app.Windows)
                {
                    dynamic w = window;
                    if (!(bool)w.IsVisible) continue;
                    object? peer = create.Invoke(null, new[] { window });
                    if (peer != null) Walk(peer, 0, (string)(w.Title ?? ""), left, top, result);
                }
                return null;
            };
            // Dispatcher.Invoke(Delegate, TimeSpan, object[]) — bounded so a busy UI thread cannot hang the agent.
            var invoke = dispatcher.GetType().GetMethod("Invoke", new[] { typeof(Delegate), typeof(TimeSpan), typeof(object[]) })!;
            invoke.Invoke(dispatcher, new object[] { work, TimeSpan.FromSeconds(5), Array.Empty<object>() });
            return result;
        }

        private static void Walk(object peerObj, int depth, string window, int left, int top, List<UiNode> into)
        {
            if (into.Count >= MaxNodes) return;
            dynamic peer = peerObj;
            var node = new UiNode { Depth = depth, Window = depth == 0 ? window : null };
            try
            {
                node.Role = peer.GetAutomationControlType().ToString();
                node.Name = Clip((string)peer.GetName());
                string id = peer.GetAutomationId();
                node.Id = string.IsNullOrEmpty(id) ? null : id;
                node.Enabled = peer.IsEnabled();
                node.Focused = peer.HasKeyboardFocus();
                node.Offscreen = peer.IsOffscreen();
                dynamic r = peer.GetBoundingRectangle();
                if (!(bool)r.IsEmpty)
                {
                    node.X = (int)Math.Round((double)r.X) - left; node.Y = (int)Math.Round((double)r.Y) - top;
                    node.W = (int)Math.Round((double)r.Width); node.H = (int)Math.Round((double)r.Height);
                }
                bool password = peer.IsPassword();
                node.Value = Pattern(peerObj, "Value", "IValueProvider", "Value") is string v ? (password ? "<redacted>" : Clip(v)) : null;
                if (node.Value != null && node.Value == node.Name) node.Value = null;
                node.Toggle = Pattern(peerObj, "Toggle", "IToggleProvider", "ToggleState")?.ToString();
            }
            catch (Exception ex) { node.Role = node.Role.Length > 0 ? node.Role : "?"; node.Name = "(error: " + ex.GetType().Name + ")"; }
            into.Add(node);
            IEnumerable? children = null;
            try { children = (IEnumerable?)peer.GetChildren(); } catch (Exception) { }
            if (children == null) return;
            foreach (object c in children) Walk(c, depth + 1, window, left, top, into);
        }

        private static object? Pattern(object peer, string pattern, string iface, string prop)
        {
            try
            {
                var pi = Type.GetType("System.Windows.Automation.Peers.PatternInterface, PresentationCore")!;
                object? provider = peer.GetType().GetMethod("GetPattern")!.Invoke(peer, new[] { Enum.Parse(pi, pattern) });
                if (provider == null) return null;
                // Providers implement the interfaces explicitly, so go through the interface type.
                var t = provider.GetType().GetInterfaces().FirstOrDefault(i => i.Name == iface);
                return t?.GetProperty(prop)?.GetValue(provider);
            }
            catch (Exception) { return null; }
        }
    }

    // -------------------------------------------------------------------------------------- WinForms
    private static class WinForms
    {
        public static List<UiNode> Read(int left, int top)
        {
            var result = new List<UiNode>();
            var appType = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms")!;
            var forms = ((IEnumerable)appType.GetProperty("OpenForms")!.GetValue(null)!).Cast<object>().ToList();
            foreach (object form in forms)
            {
                dynamic f = form;
                if (!(bool)f.Visible) continue;
                Func<object?> work = () => { Walk(form, 0, (string)f.Text, left, top, result); return null; };
                // BeginInvoke + bounded wait instead of Invoke, so a blocked UI thread cannot hang the agent.
                IAsyncResult ar = f.BeginInvoke(work);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("UI thread busy");
                f.EndInvoke(ar);
            }
            return result;
        }

        private static void Walk(object control, int depth, string window, int left, int top, List<UiNode> into)
        {
            if (into.Count >= MaxNodes) return;
            dynamic c = control;
            if (!(bool)c.Visible) return;
            var node = new UiNode { Depth = depth, Window = depth == 0 ? window : null };
            try
            {
                dynamic acc = c.AccessibilityObject;
                node.Role = acc.Role.ToString();
                string text = c.Text ?? "";
                string accName = acc.Name ?? "";
                bool password = control.GetType().GetProperty("UseSystemPasswordChar")?.GetValue(control) is true
                    || (control.GetType().GetProperty("PasswordChar")?.GetValue(control) is char pc && pc != '\0');
                bool editable = node.Role == "Text" && control.GetType().GetProperty("ReadOnly") != null;
                if (editable) { node.Role = "Edit"; node.Name = Clip(accName); node.Value = password ? "<redacted>" : Clip(text); }
                else node.Name = Clip(accName.Length > 0 ? accName : text);
                string name = c.Name ?? "";
                node.Id = name.Length > 0 ? name : null;
                node.Enabled = c.Enabled;
                node.Focused = c.Focused;
                if (control.GetType().GetProperty("Checked")?.GetValue(control) is bool on) node.Toggle = on ? "On" : "Off";
                dynamic b = acc.Bounds;
                node.X = (int)b.X - left; node.Y = (int)b.Y - top; node.W = (int)b.Width; node.H = (int)b.Height;
            }
            catch (Exception ex) { node.Role = "?"; node.Name = "(error: " + ex.GetType().Name + ")"; }
            into.Add(node);
            foreach (object child in (IEnumerable)c.Controls) Walk(child, depth + 1, window, left, top, into);
        }
    }

    /// <summary>Polls until an element matching the text exists (assertion for agents).</summary>
    public static UiNode? WaitFor(string text, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var hit = Find(Read().Nodes, text);
            if (hit != null || sw.ElapsedMilliseconds >= timeoutMs) return hit;
            Thread.Sleep(150);
        }
    }
}
