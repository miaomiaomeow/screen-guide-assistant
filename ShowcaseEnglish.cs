using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenGuide;

internal static class ShowcaseEnglish
{
    private static readonly Dictionary<string,string> Labels = new()
    {
        ["屏幕指路助手"]="Screen Guide", ["指路助手"]="Screen Guide",
        ["控制面板"]="Control panel", ["模型配置"]="Connection settings", ["助手回复"]="Guide reply",
        ["返回"]="Back", ["返回设置"]="Back", ["想找什么功能？"]="What would you like to find?",
        ["指路"]="Guide", ["语音"]="Voice", ["语音对话"]="Voice chat", ["开启语音对话"]="Start voice chat",
        ["停止对话"]="Stop chat", ["停止语音对话"]="Stop voice chat", ["结束"]="Stop", ["转写中"]="Transcribing",
        ["本地转写"]="Local · English", ["语音识别"]="Speech recognition", ["本地运行，不上传录音"]="Local audio · English",
        ["屏幕读取"]="Screen input", ["你决定是否发送图片"]="Choose what to share",
        ["混合：控件＋截图（默认）"]="Hybrid: controls + image", ["仅控件（不发图）"]="Controls only",
        ["直接发送截图"]="Image every step", ["控件失败时截图"]="Image on control failure",
        ["手动：控件原名"]="Manual: exact control name", ["手动调试 · 不调用模型"]="Manual · no model calls",
        ["模型配置与切换"]="Connection settings", ["清除高亮"]="Clear highlight", ["重置本轮"]="Reset guide",
        ["贴边收起"]="Dock", ["退出"]="Quit", ["模型未配置"]="Connection not configured",
        ["目标：先切到软件"]="Target: switch to your app", ["新建"]="New", ["使用所选"]="Use selected",
        ["删除"]="Delete", ["方案名称"]="Profile name", ["模型名称"]="Actual model ID", ["保存并使用"]="Save and use",
        ["清除已保存的密钥"]="Clear stored key", ["已保存的方案"]="Saved connections", ["切换立即生效"]="Applied immediately",
        ["兼容接口基础地址（HTTPS，不含 /chat/completions）"]="API base URL (HTTPS)",
        ["密钥环境变量（可选，不显示变量内容）"]="Key environment variable (optional)",
        ["新密钥（可选，Windows 用户加密保存；留空保留）"]="New key (optional; encrypted for this Windows user)",
        ["单击控制面板 · 拖动换边"]="Click to expand · click again for settings · drag to dock",
        ["贴边收起 · Esc 清除高亮"]="Dock · Esc clears the highlight",
        ["Ctrl+Shift+G / Ctrl+Alt+G · 不用移开鼠标"]="Ctrl+Shift+G / Ctrl+Alt+G · keep the pointer in place",
        ["正在听 · 停顿自动发送"]="Listening · pause to send", ["本地转写 · 可暂停等待"]="Transcribing locally",
        ["正在指路 · 可暂停等待"]="Finding the next entry", ["等待上一段完成"]="Waiting for the previous turn",
        ["设置会自动保存；启动时麦克风始终关闭。"]="Settings save automatically. Microphone starts off.",
        ["设置已保存 · 启动时麦克风始终关闭"]="Settings saved · microphone starts off",
        ["当前 Agent 模型"]="Current agent connection"
    };
    internal static string Text(string value)
    {
        if (!Regex.IsMatch(value,"[\\u4e00-\\u9fff]")) return value;
        if (Labels.TryGetValue(value,out var result)) return result;
        if (value.StartsWith("目标：")) return "Target: " + value[3..];
        if (value.StartsWith("你：")) return "You: " + (value[2..].Contains("说一句") ? "Say the feature you want to find." : value[2..]);
        if (value.Contains("遮挡") || value.Contains("浮窗")) return "Image blocked by a floating window. Move it aside, then retry.";
        if (value.Contains("过期") || value.Contains("已变化") || value.Contains("已移动")) return "The view changed. Read the target again before continuing.";
        if (value.Contains("切回") || value.Contains("切换") || value.Contains("目标窗口")) return "Switch back to the bound target app, then continue.";
        if (value.Contains("缺少") && value.Contains("模型")) return "English speech model is missing. Prepare the SenseVoice files.";
        if (value.Contains("未配置") || value.Contains("密钥")) return "Configure a working connection, or use the agent/manual mode.";
        if (value.Contains("录音中") || value.Contains("录音已开始")) return "Recording English locally. Stop when finished; Esc cancels.";
        if (value.Contains("转写已填入")) return "Transcript ready. Review it, then click Guide.";
        if (value.Contains("转写") && !value.Contains("失败")) return "Transcribing English locally. Esc cancels.";
        if (value.Contains("麦克风") && value.Contains("失败") || value.Contains("麦克风无法")) return "Unable to open the microphone. Check the input device and permissions.";
        if (value.Contains("无法") || value.Contains("失败") || value.Contains("不支持")) return "Unable to complete this step. Check the target, connection and model files, then retry.";
        if (value.Contains("已清除") || value.Contains("已取消")) return "Highlight cleared. Start or resume guidance when ready.";
        if (value.Contains("已指出") || value.Contains("白框为") || value.Contains("手动指出"))
        {
            var label=Regex.Match(value,"「(.*?)」");
            return (label.Success ? "Find: " + label.Groups[1].Value + ". " : "Entry highlighted. ") + "Click or hover yourself to continue.";
        }
        if (value.Contains("听") || value.Contains("对话已开启")) return "Listening for English. Pause to send; Esc stops voice chat.";
        if (value.Contains("已检测到") && value.Contains("点击")) return "Click observed. Reading the updated interface…";
        if (value.Contains("已将当前") || value.Contains("等待 当前 Agent")) return "Current view shared. Waiting for the agent to choose an entry.";
        if (value.Contains("正在") || value.Contains("第 ")) return "Reading the current interface and choosing the next entry…";
        if (value.Contains("12")) return "Interaction limit reached. Reset and narrow your question.";
        if (value.Contains("已保存")) return "Settings saved.";
        if (value.Contains("已暂停") || value.Contains("已停止")) return "Guidance paused. Resume when ready.";
        if (value.Contains("预览") || value.Contains("这里显示")) return "Your guide reply appears here. You remain in control.";
        if (value.Contains("控件") || value.Contains("截图")) return "Choose a screen mode. Yellow: verified control; white: image estimate.";
        return "Ready. Ask where to find a feature; you perform every action.";
    }

    internal static void Apply(DependencyObject element)
    {
        if (element is TextBlock text) text.Text=Text(text.Text);
        if (element is ContentControl content && content.Content is string label) content.Content=Text(label);
        if (element is FrameworkElement view && view.ToolTip is string tip) view.ToolTip=Text(tip);
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++) Apply(VisualTreeHelper.GetChild(element,i));
        if (element is ComboBox combo) foreach(var item in combo.Items.OfType<ComboBoxItem>())
            if(item.Content is string caption) item.Content=Text(caption);
    }
}
