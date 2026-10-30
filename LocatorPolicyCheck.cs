namespace ScreenGuide;

internal static class LocatorPolicyCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var checks = new List<(string Label, bool Passed)>();
        var application = LocatorPolicy.Enter(default, "Window");
        var titleBar = LocatorPolicy.Enter(application, "TitleBar");
        var chromeButton = LocatorPolicy.Enter(titleBar, "Button");
        var nestedChrome = LocatorPolicy.Enter(LocatorPolicy.Enter(titleBar, "Group"), "MenuItem");
        checks.Add(("窗口本身不被当成标题栏外壳", !application.InTitleBar));
        checks.Add(("TitleBar角色本身标记窗口外壳", titleBar.InTitleBar));
        checks.Add(("标题栏按钮继承外壳分类", chromeButton.InTitleBar));
        checks.Add(("标题栏多层后代仍继承外壳分类", nestedChrome.InTitleBar));
        checks.Add(("外壳分类不会泄漏到旁边业务菜单", !LocatorPolicy.Enter(application, "MenuBar").InTitleBar));
        checks.Add(("标题栏按钮仍保留给手动调试", LocatorPolicy.CandidateRejection("Button", chromeButton, false) is null));
        checks.Add(("业务按钮保持原有候选资格", LocatorPolicy.CandidateRejection("Button", application, false) is null));
        checks.Add(("原支持的菜单和输入选择入口仍保留", new[] { "MenuItem", "CheckBox", "RadioButton", "ComboBox", "Hyperlink", "SplitButton" }
            .All(role => LocatorPolicy.CandidateRejection(role, application, false) is null)));
        checks.Add(("业务树分类仅在确实可展开时纳入", LocatorPolicy.CandidateRejection("TreeItem", application, true) is null));
        checks.Add(("树叶或无展开能力的树项被排除", LocatorPolicy.CandidateRejection("TreeItem", application, false) == "tree-not-expandable"));

        var document = LocatorPolicy.Enter(application, "Document");
        var documentTree = LocatorPolicy.Enter(LocatorPolicy.Enter(document, "Group"), "TreeItem");
        var edit = LocatorPolicy.Enter(application, "Edit");
        var editTree = LocatorPolicy.Enter(LocatorPolicy.Enter(edit, "Group"), "TreeItem");
        checks.Add(("文档祖先标记跨中间容器继承", documentTree.InDocument));
        checks.Add(("编辑祖先标记跨中间容器继承", editTree.InEdit));
        checks.Add(("文档内可展开树项仍不发送正文结构", LocatorPolicy.CandidateRejection("TreeItem", documentTree, true) == "tree-in-document"));
        checks.Add(("编辑区内可展开树项仍被排除", LocatorPolicy.CandidateRejection("TreeItem", editTree, true) == "tree-in-document"));
        checks.Add(("文档和标题栏祖先可独立保留", LocatorPolicy.Enter(titleBar, "Document") is { InTitleBar: true, InDocument: true }));
        checks.Add(("文档祖先不会污染另一个业务分支", !LocatorPolicy.Enter(application, "TreeItem").InDocument));
        checks.Add(("本轮不把可能含文档标题的TabItem纳入", LocatorPolicy.CandidateRejection("TabItem", application, true) == "non-entry-role"));
        checks.Add(("本轮不把列表内容或普通文本纳入", new[] { "ListItem", "Text", "Document", "Edit", "Pane", "Custom", "TitleBar" }
            .All(role => LocatorPolicy.CandidateRejection(role, application, true) == "non-entry-role")));
        return checks.AsReadOnly();
    }
}
