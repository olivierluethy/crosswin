using CrossWin;

var env = new HostEnv();
string repo = Environment.GetEnvironmentVariable("CROSSWIN_REPO") ?? AppContext.BaseDirectory;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        CrossWin — run existing WinForms/WPF apps on macOS and Linux via Wine.

        Usage:
          crosswin doctor                      Check host, Rosetta, .NET SDK, Wine and prefix
          crosswin setup                       Install/prepare Wine, the Wine prefix and the CrossWin agent
          crosswin inspect <repo|csproj>       Report UI framework, target framework, backend and risks
          crosswin run <repo|csproj> [options] [-- app args]
              --project <name>   pick a project when the repo contains several apps
              --gpu              let WPF use hardware rendering (default: software, more reliable)
              --snap-after <s>   save a screenshot of the app windows <s> seconds after start
              --no-build         reuse the previous build output
          crosswin snap                        Screenshot the windows of the running app (PNG)
          crosswin input "click X Y" "wait MS" "type TEXT" "key Enter|Tab|Escape|Down|..."
                                               Send real mouse/keyboard input to the running app
                                               (X/Y = pixels in the snap image of the window client area)
                                               "click <label>" clicks a control by its visible text,
                                               "expect <text>" asserts it appears (3 s); a batch stops at
                                               the first failure
          crosswin ui [--all] [--json]         Semantic UI tree (role, name, value, bounds) from WPF
                                               automation peers / WinForms accessibility — no screenshot
          crosswin events [--level warn|error] [--kind K] [--since SEQ] [--last N] [--json]
                                               Structured session events: build errors with file/line,
                                               unhandled exceptions with stack, app output, interactions
          crosswin session [--json]            Target identity (project, revision, runtime), state, counts
          crosswin stop                        Stop the running app of the current session
          crosswin mcp                         Optional MCP server (stdio) for AI agent hosts; same sessions

          crosswin mobile …                    Mobile apps (Flutter, React Native, Android) without
                                               Android Studio or Xcode — see 'crosswin mobile help'
          crosswin web …                       Run a local web app (URL, .html or dev server) in a browser and
                                               stream its console/errors/network — see 'crosswin web help'

        Environment: CROSSWIN_HOME (default ~/.crosswin), CROSSWIN_WINE, WINEPREFIX, WINEDEBUG
        """);
    return 0;
}

try
{
    return args[0] switch
    {
        "doctor" => Commands.Doctor(env),
        "setup" => Commands.Setup(env, repo),
        "inspect" when args.Length > 1 => Commands.Inspect(env, args[1]),
        "run" when args.Length > 1 => Commands.Run(env, repo, args[1], args.Skip(2).ToArray()),
        "snap" => Commands.Snap(env),
        "ui" => Commands.Ui(env, args.Skip(1).ToArray()),
        "events" => Commands.Events(env, args.Skip(1).ToArray()),
        "session" => Commands.SessionStatus(env, args.Skip(1).ToArray()),
        "stop" => Commands.Stop(env),
        "mcp" => Mcp.Serve(env, repo),
        "input" when args.Length > 1 => Commands.Input(env, args.Skip(1).ToArray()),
        "mobile" => CrossWin.Mobile.MobileCommands.Dispatch(env, args.Skip(1).ToArray()),
        "web" => CrossWin.Web.WebCommands.Dispatch(env, args.Skip(1).ToArray()),
        _ => Fail($"Unknown or incomplete command: {string.Join(' ', args)}. Run 'crosswin help'."),
    };
}
catch (CrossWinException ex)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"crosswin: {message}");
    return 1;
}

public class CrossWinException(string message) : Exception(message);
