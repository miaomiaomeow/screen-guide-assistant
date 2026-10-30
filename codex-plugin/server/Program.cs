using System.Text;

namespace ScreenGuide.Plugin;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 1 && args[0] == "--self-test") return await SelfTest.RunAsync();
        if (args.Length != 0)
        {
            await Console.Error.WriteLineAsync("ScreenGuide.Plugin accepts no arguments, or --self-test.");
            return 2;
        }
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        try
        {
            var client = new PipeGuideClient(PipeIdentity.CurrentUserName());
            await new McpServer(client).RunAsync(Console.In, Console.Out, stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error)
        {
            // Standard output is reserved for MCP, and diagnostics never contain payloads.
            await Console.Error.WriteLineAsync("Screen guide MCP stopped: " + error.GetType().Name);
            return 1;
        }
    }
}
