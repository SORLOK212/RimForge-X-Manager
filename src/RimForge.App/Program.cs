using Avalonia;
using RimForge.Steam.Client;

namespace RimForge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The same exe doubles as the small Steam helper process (see HelperProtocol).
        if (args.Length > 0 && args[0] == HelperProtocol.Argument) return SteamHelperHost.Run();
        // ...and as the MCP server for AI assistants, started by the AI app (stdin/stdout belong to it).
        if (args.Length > 0 && args[0] == "--mcp") return Mcp.McpServer.RunAsync().GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
