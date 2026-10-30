using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;

namespace ScreenGuide;

internal sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--english-speech-check") { Environment.ExitCode = EnglishSpeechCheck.Run(args[1]).GetAwaiter().GetResult(); return; }
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfCheck.Run(); return; }
        if (args.Contains("--profile-secret-check"))
        { Environment.ExitCode = ModelProfilesCheck.DpapiRoundTrip() ? 0 : 1; return; }
        if (args.Contains("--plugin-bridge-check"))
        { Environment.ExitCode = PluginBridgeCheck.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--speech-endpoint-check")) { Environment.ExitCode = SpeechEndpointCheck.Run(); return; }
        if (args.Contains("--ui-preview"))
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Environment.ExitCode = ScreenGuide.MainWindow.RenderShellPreviews(); return;
        }
        if (args.Contains("--speech-check"))
        {
            var index = Array.IndexOf(args, "--speech-check");
            var engineIndex = Array.IndexOf(args, "--speech-engine");
            var engine = engineIndex >= 0 && engineIndex + 1 < args.Length ? args[engineIndex + 1] : null;
            Environment.ExitCode = index + 1 < args.Length ? SpeechCheck.Run(args[index + 1], engine, args.Contains("--speech-auto-endpoint")).GetAwaiter().GetResult() : 2; return;
        }
        if (args.Contains("--model-check")) { Environment.ExitCode = ModelCheck.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--vision-check")) { Environment.ExitCode = VisionCheck.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--model-replay"))
        {
            var index = Array.IndexOf(args, "--model-replay");
            Environment.ExitCode = index + 1 < args.Length ? ModelReplay.Run(args[index + 1]).GetAwaiter().GetResult() : 2; return;
        }
        if (args.Contains("--inspect") || args.Contains("--inspect-window"))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "generic-inspection.json");
            try
            {
                var target = Native.RootTarget(Native.GetForegroundWindow());
                var windowArg = Array.IndexOf(args, "--inspect-window");
                if (windowArg >= 0 && windowArg + 1 < args.Length && long.TryParse(args[windowArg + 1], out var handle)) target = new nint(handle);
                var diagnostics = new List<object>();
                var snapshot = Locator.Capture(target, diagnostics);
                var assistantWindows = new List<object>();
                Native.EnumWindows((h, _) =>
                {
                    try
                    {
                        Native.GetWindowThreadProcessId(h, out var pid);
                        if (Process.GetProcessById((int)pid).ProcessName == "ScreenGuide" && Native.IsWindowVisible(h))
                        {
                            var name = AutomationElement.FromHandle(h).Current.Name;
                            Native.GetWindowRect(h, out var r); var rect = r.ToRect();
                            assistantWindows.Add(new { name, rect = rect.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                exStyle = $"0x{Native.GetWindowLongPtr(h, Native.GwlExStyle).ToInt64():X}", foreground = Native.GetForegroundWindow() == h,
                                centerHitTarget = Native.BelongsTo(Native.PointWindow(rect), target) });
                        }
                    }
                    catch { }
                    return true;
                }, 0);
                var report = new { os = Environment.OSVersion.ToString(), window = target.ToInt64(), context = snapshot.Context, diagnostics,
                    capture = new { snapshot.PopupCount, snapshot.NodesRead, snapshot.ElapsedMs, snapshot.PartialReason,
                        snapshot.ApplicationCandidateCount, snapshot.WindowChromeCandidateCount, snapshot.RoleCounts, snapshot.RejectedCounts },
                    candidates = snapshot.Candidates.Select(c => new { c.Option.Id, c.IsWindowChrome, bounds = c.Bounds.ToString(System.Globalization.CultureInfo.InvariantCulture) }), assistantWindows };
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (Exception ex) { File.WriteAllText(path, JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex is InvalidOperationException ? ex.Message : "请先让目标软件可见并位于前台。" })); Environment.ExitCode = 1; }
            return;
        }
        using var instance = new Mutex(true, "Local\\ScreenGuideDemo", out var first);
        if (!first) { MessageBox.Show("屏幕指路助手已在运行。请查看屏幕上的小条。", "屏幕指路"); return; }
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("unhandled", e.Exception.GetType().Name); e.Handled = true;
            foreach (Window window in app.Windows) if (window is OverlayWindow) window.Hide();
            MessageBox.Show("发生错误，详情已写入日志。请退出后重新启动。", "屏幕指路");
        };
        app.Run(new MainWindow(args.Contains("--acceptance")));
    }
}

internal static class Log
{
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "logs");
    internal static void Write(string action, string detail)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTimeOffset.Now:O} {action} {detail}{Environment.NewLine}");
        }
        catch { }
    }
}
