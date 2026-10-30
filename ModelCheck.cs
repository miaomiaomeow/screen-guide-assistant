using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScreenGuide;

internal static class ModelCheck
{
    internal static async Task<int> Run()
    {
        var config = new ModelProfileStore().LoadActiveOrLegacy(); if (!config.Ready) return 2;
        var mapper = new IntentMapper(config);
        var editor = new SelectionContext("generic-editor", false, [new("s1-01", "设置", "Button", "ToolBar", true, false), new("s1-02", "编辑", "MenuItem", "MenuBar", true, true), new("s1-03", "查看", "MenuItem", "MenuBar", true, true)]);
        var menu = new SelectionContext("generic-editor", false, [new("s2-01", "查找", "MenuItem", "Menu:编辑", true, false), new("s2-02", "替换", "MenuItem", "Menu:编辑", true, false)]);
        var files = new SelectionContext("generic-files", false, [new("s3-01", "刷新", "Button", "ToolBar", true, false), new("s3-02", "查看", "MenuItem", "ToolBar", true, true), new("s3-03", "详细信息", "RadioButton", "Menu:查看", true, false)]);
        var duplicate = new SelectionContext("generic-app", false, [new("s4-01", "设置", "Button", "", true, false), new("s4-02", "设置", "Button", "", true, false)]);
        var cases = new (SelectionContext Context, string Question, string? Id)[] {
            (editor, "设置入口在哪里？", "s1-01"), (editor, "偏好设置怎么打开？", "s1-01"),
            (editor, "Where are settings?", "s1-01"), (editor, "查找功能在哪？", "s1-02"),
            (menu, "怎么搜索文中的字？", "s2-01"), (menu, "替换功能在哪里？", "s2-02"),
            (files, "刷新按钮在哪里？", "s3-01"), (files, "我想用详细信息显示文件，入口在哪？", "s3-03"),
            (duplicate, "设置在哪里？", null), (editor, "它在哪？", null),
            (editor, "宇宙飞船的发射按钮在哪？", null), (editor, "给我写一首诗", null)
        };
        var results = new List<object>(); int passed = 0;
        foreach (var c in cases)
        {
            var watch = Stopwatch.StartNew(); var result = await mapper.Select(c.Question, c.Context, CancellationToken.None);
            bool ok = c.Id != null ? result.CanPoint && result.Id == c.Id : !result.CanPoint && result.Action is "not_found" or "ambiguous";
            // Network/format errors must not masquerade as a successful semantic refusal.
            if (c.Id == null && (result.Message.Contains("格式") || result.Message.Contains("服务返回") || result.Message.Contains("超时") || result.Message.Contains("连接"))) ok = false;
            if (ok) passed++;
            results.Add(new { question = c.Question, app = c.Context.App, expectedId = c.Id, actualId = result.Id, action = result.Action, passed = ok, elapsedMs = watch.ElapsedMilliseconds, result.Message });
        }
        var searchOpen = new SelectionContext("generic-editor", false, [new("s5-01", "查找下一个", "Button", "ToolBar:查找", true, false), new("s5-02", "关闭查找", "Button", "ToolBar:查找", true, false)]);
        var exportMenu = new SelectionContext("generic-editor", false, [new("s6-01", "导出", "MenuItem", "Menu:文件", true, true), new("s6-02", "打印", "MenuItem", "Menu:文件", true, false)]);
        var exportSubmenu = new SelectionContext("generic-editor", false, [.. exportMenu.Controls, new("s7-01", "PDF", "MenuItem", "Menu:文件 > Menu:导出", true, false), new("s7-02", "PNG", "MenuItem", "Menu:文件 > Menu:导出", true, false)]);
        var preferences = new SelectionContext("generic-editor", false, [new("s8-01", "首选项", "MenuItem", "Menu:文件", true, false), new("s8-02", "保存", "MenuItem", "Menu:文件", true, false)]);
        var themes = new SelectionContext("generic-editor", false, [new("s9-01", "主题", "MenuItem", "Menu:首选项", true, true), new("s9-02", "设置", "MenuItem", "Menu:首选项", true, false)]);
        var continued = new (SelectionContext Context, string Question, GuideMemory Memory, string Action, string? Id)[] {
            (menu, "查找功能在哪？", new([new(1, "编辑", "MenuItem", "MenuBar", "open_entry", "clicked")]), "point", "s2-01"),
            (menu, "查找功能在哪？", new([new(1, "编辑", "MenuItem", "MenuBar", "open_entry", "hover_revealed")]), "point", "s2-01"),
            (searchOpen, "查找功能在哪？", new([new(1, "编辑", "MenuItem", "MenuBar", "open_entry", "clicked"), new(2, "查找", "MenuItem", "Menu:编辑", "point", "clicked")]), "done", null),
            (exportMenu, "导出为 PDF 的入口在哪里？", new([new(1, "文件", "MenuItem", "MenuBar", "open_entry", "clicked")]), "open_entry", "s6-01"),
            (exportSubmenu, "导出为 PDF 的入口在哪里？", new([new(1, "文件", "MenuItem", "MenuBar", "open_entry", "clicked"), new(2, "导出", "MenuItem", "Menu:文件", "open_entry", "hover_revealed")]), "point", "s7-01"),
            (preferences, "文件菜单里的颜色主题入口在哪里？", new([new(1, "文件", "MenuItem", "MenuBar", "open_entry", "clicked")]), "open_entry", "s8-01"),
            (themes, "文件菜单里的颜色主题入口在哪里？", new([new(1, "文件", "MenuItem", "MenuBar", "open_entry", "clicked"), new(2, "首选项", "MenuItem", "Menu:文件", "open_entry", "hover_revealed")]), "open_entry", "s9-01"),
            (editor, "查找功能在哪？", new([new(1, "编辑", "MenuItem", "MenuBar", "open_entry", "clicked")]), "open_entry", "s1-02")
        };
        foreach (var c in continued)
        {
            var watch = Stopwatch.StartNew(); var result = await mapper.Select(c.Question, c.Context, CancellationToken.None, c.Memory);
            bool ok = result.Action == c.Action && result.Id == c.Id;
            if (ok) passed++;
            results.Add(new { question = c.Question, expectedAction = c.Action, expectedId = c.Id, actualId = result.Id, action = result.Action, passed = ok, interactions = c.Memory.Interactions, elapsedMs = watch.ElapsedMilliseconds, result.Message });
        }
        int total = cases.Length + continued.Length;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "generic-model-check.json"), JsonSerializer.Serialize(new { date = DateTimeOffset.Now, model = config.Model, scope = "Live model with fixed public candidate/session fixtures; not a desktop acceptance", passed, total, results }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return passed == total ? 0 : 1;
    }
}
