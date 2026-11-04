using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrossWin.Web;

/// <summary>
/// Interaction with one attached page over CDP: a compact accessibility/DOM tree in the shared node schema, element
/// lookup by visible label, and real input (CDP Input.dispatch* — actual mouse/key events, not element.click()).
/// The tree and lookup run as one Runtime.evaluate each, so a label click is two round trips (find, then click).
/// </summary>
public static class WebPage
{
    // Compact tree: role (aria role or tag-derived), accessible-ish name, value, states and viewport rect. ALLFLAG
    // is replaced with true/false; with false, only visible and "interesting" elements are kept.
    private const string TreeJs = """
        (function(all){
          function nm(el){
            var v = el.getAttribute && (el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title'));
            if(!v && el.tagName==='INPUT') v = el.getAttribute('placeholder');
            if(!v){ var t=(el.innerText||el.textContent||'').trim(); if(t.length<=80) v=t; }
            return (v||'').replace(/\s+/g,' ').trim().slice(0,120);
          }
          function role(el){
            var r=el.getAttribute && el.getAttribute('role'); if(r) return r;
            var t=el.tagName.toLowerCase();
            if(t==='input'){ var ty=(el.getAttribute('type')||'text').toLowerCase();
              if(ty==='checkbox')return'checkbox'; if(ty==='radio')return'radio';
              if(ty==='button'||ty==='submit'||ty==='reset')return'button'; return'textbox'; }
            var m={a:'link',button:'button',select:'combobox',textarea:'textbox',img:'image',nav:'navigation',
                   h1:'heading',h2:'heading',h3:'heading',h4:'heading',h5:'heading',h6:'heading'};
            return m[t]||t;
          }
          var interesting={button:1,link:1,textbox:1,checkbox:1,radio:1,combobox:1,heading:1,image:1,tab:1,menuitem:1,'switch':1};
          var out=[], max=400;
          function walk(el,depth){
            for(var i=0;i<el.children.length;i++){
              if(out.length>=max) return;
              var c=el.children[i], tag=c.tagName.toLowerCase();
              if(tag==='script'||tag==='style'||tag==='noscript'){ continue; }
              var rc=c.getBoundingClientRect(), vis=rc.width>0&&rc.height>0;
              var ro=role(c), name=nm(c);
              var clickable = ro==='button'||ro==='link'||(c.tabIndex!=null&&c.tabIndex>=0)||(c.getAttribute&&c.getAttribute('onclick')!=null);
              if((all||vis)&&(all||interesting[ro]||name.length>0)){
                var n={depth:depth,role:ro,name:name};
                if(c.id) n.id=c.id;
                if(tag==='input'||tag==='textarea'||tag==='select'){
                  var ty=(c.getAttribute('type')||'').toLowerCase(), val=(c.value||'');
                  if(ty==='password') n.value='<redacted>'; else if(val) n.value=(''+val).slice(0,200);
                }
                if(tag==='input'&&(c.type==='checkbox'||c.type==='radio')) n.toggle=c.checked?'On':'Off';
                if(c.disabled) n.enabled=false;
                if(document.activeElement===c) n.focused=true;
                if(clickable) n.clickable=true;
                n.x=Math.round(rc.left); n.y=Math.round(rc.top); n.w=Math.round(rc.width); n.h=Math.round(rc.height);
                out.push(n);
              }
              walk(c,depth+1);
            }
          }
          if(document.body) walk(document.body,0);
          return out;
        })(ALLFLAG)
        """;

    // Find by label (exact, first word, prefix, contains; case-insensitive), scroll it into view, return its centre.
    private const string FindJs = """
        (function(q){
          function nm(el){
            var v=el.getAttribute&&(el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title'));
            if(!v&&el.tagName==='INPUT') v=el.getAttribute('placeholder');
            if(!v) v=((el.value||'')||(el.innerText||el.textContent||''));
            return (''+(v||'')).replace(/\s+/g,' ').trim();
          }
          var sel='button,a,[role=button],[role=link],[role=tab],[role=menuitem],input,select,textarea,label,summary,[onclick],[tabindex]';
          var els=Array.prototype.slice.call(document.querySelectorAll(sel)), ql=q.toLowerCase();
          function pick(f){ for(var i=0;i<els.length;i++){ var n=nm(els[i]).toLowerCase(); if(n&&f(n)) return els[i]; } return null; }
          var el=pick(function(n){return n===ql;})||pick(function(n){return n.split(' ')[0]===ql;})
               ||pick(function(n){return n.indexOf(ql)===0;})||pick(function(n){return n.indexOf(ql)>=0;});
          if(!el) return null;
          el.scrollIntoView({block:'center',inline:'center'});
          var r=el.getBoundingClientRect();
          return {x:Math.round(r.left+r.width/2),y:Math.round(r.top+r.height/2),name:nm(el)};
        })(QARG)
        """;

    private static readonly Dictionary<string, (string Key, string Code, int Vk)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = ("Enter", "Enter", 13), ["return"] = ("Enter", "Enter", 13), ["tab"] = ("Tab", "Tab", 9),
        ["escape"] = ("Escape", "Escape", 27), ["esc"] = ("Escape", "Escape", 27), ["backspace"] = ("Backspace", "Backspace", 8),
        ["delete"] = ("Delete", "Delete", 46), ["up"] = ("ArrowUp", "ArrowUp", 38), ["down"] = ("ArrowDown", "ArrowDown", 40),
        ["left"] = ("ArrowLeft", "ArrowLeft", 37), ["right"] = ("ArrowRight", "ArrowRight", 39),
        ["home"] = ("Home", "Home", 36), ["end"] = ("End", "End", 35), ["space"] = (" ", "Space", 32),
    };

    public static async Task<JsonArray> Tree(Cdp cdp, string sid, bool all)
    {
        var v = await Eval(cdp, sid, TreeJs.Replace("ALLFLAG", all ? "true" : "false"));
        return (v as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
    }

    /// <summary>
    /// Runs one interaction verb: "click|tap X Y" or "click|tap LABEL", "type TEXT", "key Enter|Tab|Escape|…",
    /// "scroll up|down", "wait MS", "wait-for TEXT" / "expect TEXT". Throws on failure.
    /// </summary>
    public static async Task Do(Cdp cdp, string sid, string command)
    {
        string[] p = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        string verb = p[0].ToLowerInvariant();
        string rest = p.Length > 1 ? p[1].Trim().Trim('"') : "";
        string[] n = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool xy = n.Length == 2 && double.TryParse(n[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _) && double.TryParse(n[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        switch (verb)
        {
            case "click" or "tap" when xy:
                await Click(cdp, sid, D(n[0]), D(n[1])); break;
            case "click" or "tap" when rest.Length > 0:
            {
                var hit = await Find(cdp, sid, rest) ?? throw new CrossWinException($"No element '{rest}' on the page. Run  crosswin web ui  to see the labels.");
                await Click(cdp, sid, hit.X, hit.Y); break;
            }
            case "type" when rest.Length > 0:
                await cdp.Send("Input.insertText", new JsonObject { ["text"] = rest }, sid); break;
            case "key":
                await Key(cdp, sid, rest); break;
            case "scroll":
                await Eval(cdp, sid, $"window.scrollBy(0,{(rest == "up" ? -600 : 600)})"); break;
            case "wait" when int.TryParse(rest, out int ms):
                await Task.Delay(ms); break;
            case "wait-for" or "expect" when rest.Length > 0:
                await WaitFor(cdp, sid, rest); break;
            default:
                throw new CrossWinException($"Unknown input '{command}'. Use: click X Y | click LABEL | type TEXT | key Enter|Tab|Escape | scroll up|down | wait MS | wait-for TEXT");
        }
    }

    public static async Task<(double X, double Y, string Name)?> Find(Cdp cdp, string sid, string label)
    {
        var v = await Eval(cdp, sid, FindJs.Replace("QARG", JsonSerializer.Serialize(label)));
        if (v is not JsonObject o) return null;
        return (D(o["x"]), D(o["y"]), o["name"]?.ToString() ?? label);
    }

    private static async Task Click(Cdp cdp, string sid, double x, double y)
    {
        foreach (var (type, buttons) in new[] { ("mousePressed", 1), ("mouseReleased", 0) })
            await cdp.Send("Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["buttons"] = buttons, ["clickCount"] = 1,
            }, sid);
    }

    private static async Task Key(Cdp cdp, string sid, string name)
    {
        if (!Keys.TryGetValue(name.Trim(), out var k))
            throw new CrossWinException($"Unknown key '{name}'. Known: {string.Join(", ", Keys.Keys)}");
        foreach (string type in new[] { "keyDown", "keyUp" })
            await cdp.Send("Input.dispatchKeyEvent", new JsonObject
            {
                ["type"] = type, ["key"] = k.Key, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.Vk, ["nativeVirtualKeyCode"] = k.Vk,
            }, sid);
    }

    private static async Task WaitFor(Cdp cdp, string sid, string text)
    {
        string expr = $"(function(q){{return (document.body&&document.body.innerText||'').toLowerCase().indexOf(q.toLowerCase())>=0;}})({JsonSerializer.Serialize(text)})";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await Eval(cdp, sid, expr) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.True) return;
            await Task.Delay(300);
        }
        throw new CrossWinException($"'{text}' did not appear within 15 s.");
    }

    private static async Task<JsonNode?> Eval(Cdp cdp, string sid, string expression)
    {
        var r = await cdp.Send("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true }, sid);
        if (r["exceptionDetails"] is JsonObject ed)
            throw new CrossWinException("page script error: " + (ed["exception"]?["description"]?.ToString() ?? ed["text"]?.ToString() ?? "unknown"));
        return r["result"]?["value"];
    }

    private static double D(JsonNode? node) => node != null && double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
