namespace ScreenGuide;

internal static class VoiceChatStateCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var checks = new List<(string Label, bool Passed)>();
        var flow = new VoiceChatState();
        checks.Add(("语音对话默认关闭且没有目标", !flow.Enabled && flow.Phase == VoiceChatPhase.Off
            && flow.Target == 0 && !flow.Current(flow.Generation)));
        long generation = flow.Start((nint)101);
        checks.Add(("明确开启后等待收音并绑定当前目标", flow.Enabled && flow.Phase == VoiceChatPhase.Waiting
            && flow.Target == (nint)101 && flow.Current(generation)));
        bool rejectedEmptyTarget;
        try { flow.Start(0); rejectedEmptyTarget = false; }
        catch (ArgumentOutOfRangeException) { rejectedEmptyTarget = true; }
        checks.Add(("空目标开启被拒绝且不破坏现有会话", rejectedEmptyTarget && flow.Current(generation)
            && flow.Target == (nint)101 && flow.Phase == VoiceChatPhase.Waiting));
        checks.Add(("等待收音不能跳过录音或直接提交引导", !flow.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Transcribing)
            && !flow.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Guiding) && flow.Phase == VoiceChatPhase.Waiting));
        checks.Add(("等待状态可进入收音", flow.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Listening)
            && flow.Phase == VoiceChatPhase.Listening));
        checks.Add(("重复启动本轮收音不会重复推进状态", !flow.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Listening)
            && flow.Phase == VoiceChatPhase.Listening));
        checks.Add(("收音不能跳过本地转写直接引导", !flow.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Guiding)
            && flow.Phase == VoiceChatPhase.Listening));
        checks.Add(("首次分句端点进入转写", flow.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing)
            && flow.Phase == VoiceChatPhase.Transcribing));
        checks.Add(("重复分句端点不能重复提交", !flow.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing)
            && flow.Phase == VoiceChatPhase.Transcribing));
        checks.Add(("转写中不能跳过本句引导直接重开收音", !flow.Move(generation, VoiceChatPhase.Transcribing, VoiceChatPhase.Listening)
            && !flow.Move(generation, VoiceChatPhase.Transcribing, VoiceChatPhase.Waiting)
            && flow.Phase == VoiceChatPhase.Transcribing));
        checks.Add(("转写完成后进入本句引导", flow.Move(generation, VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding)
            && flow.Phase == VoiceChatPhase.Guiding));
        checks.Add(("引导中不能直接打开下一段收音", !flow.Move(generation, VoiceChatPhase.Guiding, VoiceChatPhase.Listening)
            && flow.Phase == VoiceChatPhase.Guiding));
        checks.Add(("首轮引导返回后进入等待", flow.Move(generation, VoiceChatPhase.Guiding, VoiceChatPhase.Waiting)
            && flow.Phase == VoiceChatPhase.Waiting));
        checks.Add(("下一句沿用同次开启的目标和generation", flow.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Listening)
            && flow.Current(generation) && flow.Generation == generation && flow.Target == (nint)101));
        checks.Add(("非法枚举状态不能改变当前收音", !flow.Move(generation, VoiceChatPhase.Listening, (VoiceChatPhase)999)
            && !flow.Move(generation, (VoiceChatPhase)999, VoiceChatPhase.Transcribing)
            && flow.Phase == VoiceChatPhase.Listening && flow.Target == (nint)101));
        flow.Stop();
        checks.Add(("停止收音后立刻关闭并失效旧generation", !flow.Enabled && flow.Phase == VoiceChatPhase.Off
            && flow.Target == 0 && !flow.Current(generation) && flow.Generation > generation));
        checks.Add(("停止后迟到端点不能重新进入转写", !flow.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing)
            && !flow.Enabled && flow.Target == 0));

        var transcribing = AtTranscribing((nint)202);
        long transcribingGeneration = transcribing.Generation;
        transcribing.Stop();
        checks.Add(("转写处理中停止后旧结果不能进入引导", !transcribing.Move(transcribingGeneration,
            VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding) && transcribing.Phase == VoiceChatPhase.Off && transcribing.Target == 0));
        var guiding = AtTranscribing((nint)303);
        long guidingGeneration = guiding.Generation;
        guiding.Move(guidingGeneration, VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding);
        guiding.Stop();
        checks.Add(("引导处理中停止后finally不能重新等待收音", !guiding.Move(guidingGeneration,
            VoiceChatPhase.Guiding, VoiceChatPhase.Waiting) && !guiding.Enabled && guiding.Target == 0));

        long reopenedGeneration = flow.Start((nint)404);
        flow.Move(reopenedGeneration, VoiceChatPhase.Waiting, VoiceChatPhase.Listening);
        checks.Add(("停后重开绑定新目标并产生新generation", flow.Current(reopenedGeneration)
            && reopenedGeneration > generation && flow.Target == (nint)404 && flow.Phase == VoiceChatPhase.Listening));
        checks.Add(("新旧同为收音时旧端点仍不能推进新会话", !flow.Move(generation,
            VoiceChatPhase.Listening, VoiceChatPhase.Transcribing)
            && flow.Current(reopenedGeneration) && flow.Phase == VoiceChatPhase.Listening && flow.Target == (nint)404));
        flow.Move(reopenedGeneration, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing);
        checks.Add(("新旧同为转写时旧识别结果不能覆盖新会话", !flow.Move(generation,
            VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding)
            && flow.Current(reopenedGeneration) && flow.Phase == VoiceChatPhase.Transcribing && flow.Target == (nint)404));
        flow.Stop(); flow.Stop();
        checks.Add(("重复停止保持关闭且所有旧会话仍失效", flow.Phase == VoiceChatPhase.Off && flow.Target == 0
            && !flow.Current(generation) && !flow.Current(reopenedGeneration) && !flow.Current(flow.Generation)));
        return checks.AsReadOnly();
    }

    private static VoiceChatState AtTranscribing(nint target)
    {
        var state = new VoiceChatState();
        long generation = state.Start(target);
        state.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Listening);
        state.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing);
        return state;
    }
}
