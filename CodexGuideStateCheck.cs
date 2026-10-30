namespace ScreenGuide;

internal static class CodexGuideStateCheck
{
    internal static async Task<IReadOnlyList<(string Label, bool Passed)>> RunChecksAsync()
    {
        var checks = new List<(string Label, bool Passed)>();
        var state = new CodexGuideState("synthetic-session");
        checks.Add(("Codex 事件状态初始为空且不保存桌面对象", state.Id == "synthetic-session"
            && state.Cursor == 0 && !state.Terminal));

        var first = state.Publish("highlighted", "Synthetic visible control");
        var replay = await state.WaitAsync(0, 1, CancellationToken.None);
        checks.Add(("先发布再等待仍能读取事件", first.Cursor == 1 && state.Cursor == 1
            && replay == first && replay.Kind == "highlighted"));

        var waiter1 = state.WaitAsync(first.Cursor, 1, CancellationToken.None);
        var waiter2 = state.WaitAsync(first.Cursor, 1, CancellationToken.None);
        checks.Add(("无新事件时两个等待者均保持等待", !waiter1.IsCompleted && !waiter2.IsCompleted));
        var clicked = state.Publish("clicked");
        var results = await Task.WhenAll(waiter1, waiter2).WaitAsync(TimeSpan.FromSeconds(2));
        checks.Add(("多个等待者均收到同一新事件", results.All(item => item == clicked)));

        var third = state.Publish("screen_changed");
        var fromStart = await state.WaitAsync(0, 1, CancellationToken.None);
        var fromFirst = await state.WaitAsync(first.Cursor, 1, CancellationToken.None);
        var fromSecond = await state.WaitAsync(clicked.Cursor, 1, CancellationToken.None);
        checks.Add(("不同游标按顺序重放第一个未读事件", fromStart == first && fromFirst == clicked && fromSecond == third));

        var noEvent = await state.WaitAsync(third.Cursor, 1, CancellationToken.None);
        checks.Add(("没有事件的有限等待返回空且不改变状态", noEvent is null && state.Cursor == third.Cursor && !state.Terminal));

        using (var cancel = new CancellationTokenSource())
        {
            var cancelledWait = state.WaitAsync(third.Cursor, 1, cancel.Token);
            cancel.Cancel();
            bool threwCancellation;
            try { await cancelledWait; threwCancellation = false; }
            catch (OperationCanceledException) { threwCancellation = true; }
            checks.Add(("取消等待抛出取消异常", threwCancellation));
        }
        var followingWait = state.WaitAsync(third.Cursor, 1, CancellationToken.None);
        var next = state.Publish("hover_completed");
        checks.Add(("取消一个等待者不破坏后续等待", await followingWait == next));

        using (var oneCancelled = new CancellationTokenSource())
        {
            var cancelledWait = state.WaitAsync(next.Cursor, 1, oneCancelled.Token);
            var survivingWait = state.WaitAsync(next.Cursor, 1, CancellationToken.None);
            oneCancelled.Cancel();
            try { await cancelledWait; } catch (OperationCanceledException) { }
            var survivingEvent = state.Publish("screen_changed");
            checks.Add(("取消一个并发等待者不会取消另一个", await survivingWait == survivingEvent));
            next = survivingEvent;
        }

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            bool threwCancellation;
            try { await state.WaitAsync(0, 1, alreadyCancelled.Token); threwCancellation = false; }
            catch (OperationCanceledException) { threwCancellation = true; }
            checks.Add(("已取消的等待即使存在历史事件也抛出取消异常", threwCancellation));
        }

        var terminalWait = state.WaitAsync(next.Cursor, 1, CancellationToken.None);
        var finished = state.Finish("cleared", "Synthetic user cancellation");
        checks.Add(("终态追加事件并唤醒等待者", state.Terminal && await terminalWait == finished
            && finished.Cursor == next.Cursor + 1 && state.Cursor == finished.Cursor));
        var afterTerminal = state.WaitAsync(finished.Cursor, 25, CancellationToken.None);
        checks.Add(("终态已读后立即返回空", afterTerminal.IsCompleted && await afterTerminal is null));
        checks.Add(("重复结束不覆盖终态事件或增加游标", state.Finish("ignored") == finished && state.Cursor == finished.Cursor));
        bool refusedPublish;
        try { state.Publish("late"); refusedPublish = false; }
        catch (InvalidOperationException) { refusedPublish = true; }
        checks.Add(("终态拒绝迟到事件", refusedPublish && state.Cursor == finished.Cursor));

        var bounded = new CodexGuideState("bounded-session");
        for (int i = 1; i <= 40; i++) bounded.Publish("synthetic");
        long after = 0; int count = 0; long oldest = 0;
        while (after < bounded.Cursor)
        {
            var item = await bounded.WaitAsync(after, 1, CancellationToken.None);
            if (item is null) break;
            if (count == 0) oldest = item.Cursor;
            count++; after = item.Cursor;
        }
        checks.Add(("事件日记只保留最近三十二条且游标不重置", count == 32 && oldest == 9 && after == 40 && bounded.Cursor == 40));

        bool concurrentCursors;
        var concurrent = new CodexGuideState("concurrent-session");
        var publications = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => concurrent.Publish("synthetic"))));
        concurrentCursors = publications.Select(item => item.Cursor).Order().SequenceEqual(Enumerable.Range(1, 64).Select(i => (long)i));
        checks.Add(("并发发布产生连续且唯一的游标", concurrentCursors && concurrent.Cursor == 64));

        checks.Add(("等待时间和游标边界会被校验", await InvalidWait(bounded, -1, 1)
            && await InvalidWait(bounded, 0, 0) && await InvalidWait(bounded, 0, 26)));
        return checks.AsReadOnly();
    }

    private static async Task<bool> InvalidWait(CodexGuideState state, long after, int seconds)
    {
        try { await state.WaitAsync(after, seconds, CancellationToken.None); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
}
