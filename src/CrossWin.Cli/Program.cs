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
        "input" when args.Length > 1 => Commands.Input(env, args.Skip(1).ToArray()),
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

public sealed class CrossWinException(string message) : Exception(message);
