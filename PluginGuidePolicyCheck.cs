using System.Text.Json;

namespace ScreenGuide;

internal static class PluginGuidePolicyCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var checks = new List<(string Label, bool Passed)>();
        void Accept(string label, string json, string command)
        { try { checks.Add((label, PluginGuidePolicy.Parse(json).Command == command)); } catch { checks.Add((label, false)); } }
        void Reject(string label, string json)
        { try { PluginGuidePolicy.Parse(json); checks.Add((label, false)); } catch (JsonException) { checks.Add((label, true)); } }
        Accept("plugin status request has no screen side effects", "{\"command\":\"status\"}", "status");
        Accept("plugin target list request accepted", "{\"command\":\"list_targets\"}", "list_targets");
        Accept("plugin binding accepts opaque short target ID", "{\"command\":\"bind_target\",\"targetId\":\"aabbcc0011223344\"}", "bind_target");
        Accept("plugin guide may omit mode to follow saved preference", "{\"command\":\"guide\",\"question\":\"设置在哪\"}", "guide");
        Accept("plugin accepts explicitly selected fallback mode", "{\"command\":\"guide\",\"question\":\"设置在哪\",\"mode\":\"image_on_failure\"}", "guide");
        Accept("plugin optional null fields are compatible with typed client", "{\"command\":\"status\",\"targetId\":null,\"question\":null,\"mode\":null}", "status");
        foreach (var (label, json) in new (string, string)[] {
            ("plugin disallows coordinates", "{\"command\":\"guide\",\"question\":\"Find\",\"x\":1}"),
            ("plugin disallows automatic click command", "{\"command\":\"click\"}"),
            ("plugin disallows microphone command", "{\"command\":\"record\"}"),
            ("plugin disallows duplicated command", "{\"command\":\"status\",\"command\":\"clear\"}"),
            ("plugin disallows missing command", "{}"),
            ("plugin disallows command of wrong type", "{\"command\":1}"),
            ("plugin disallows invalid guide mode", "{\"command\":\"guide\",\"question\":\"Find\",\"mode\":\"auto\"}"),
            ("plugin disallows blank questions", "{\"command\":\"guide\",\"question\":\"  \"}"),
            ("plugin disallows control characters", "{\"command\":\"guide\",\"question\":\"Find\\nNow\"}"),
            ("plugin disallows arbitrary handles", "{\"command\":\"bind_target\",\"targetId\":\"1234\"}"),
            ("plugin disallows smuggling guide into status", "{\"command\":\"status\",\"question\":\"Find\"}"),
            ("plugin disallows target in guide instead of explicit binding", "{\"command\":\"guide\",\"question\":\"Find\",\"targetId\":\"aabbcc0011223344\"}"),
            ("plugin disallows request array", "[]") }) Reject(label, json);
        Reject("plugin limits question length", JsonSerializer.Serialize(new { command = "guide", question = new string('x', 501) }));
        checks.Add(("plugin deadline cancels at expiry", PluginGuidePolicy.Expired(60000, 60000) && !PluginGuidePolicy.Expired(59999, 60000)));
        checks.Add(("plugin fresh default is hybrid", PluginGuidePolicy.TryMode(null, out var mode) && mode == GuideMode.Hybrid));
        foreach (var preferred in Enum.GetValues<GuideMode>())
        {
            checks.Add(("plugin missing mode follows saved preference " + preferred, PluginGuidePolicy.ResolveMode(null, preferred) == preferred));
            checks.Add(("plugin explicit controls overrides saved preference " + preferred, PluginGuidePolicy.ResolveMode("controls", preferred) == GuideMode.Controls));
            checks.Add(("plugin explicit hybrid overrides saved preference " + preferred, PluginGuidePolicy.ResolveMode("hybrid", preferred) == GuideMode.Hybrid));
        }
        Accept("plugin accepts hybrid start", "{\"command\":\"codex_start\",\"question\":\"设置在哪\",\"mode\":\"hybrid\"}", "codex_start");
        foreach (var (label, json, command) in new (string, string, string)[] {
            ("Codex starts a session without a desktop model request", """{"command":"codex_start","question":"查找在哪里"}""", "codex_start"),
            ("Codex explicitly selects direct image mode", """{"command":"codex_start","question":"查找在哪里","mode":"direct_image"}""", "codex_start"),
            ("Codex reads only a named session", """{"command":"codex_read","sessionId":"session-1"}""", "codex_read"),
            ("Codex points to a current candidate", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","candidateId":"entry_1","confidence":0.9}""", "codex_point"),
            ("Codex can request a menu entry", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"open_entry","candidateId":"entry_1","label":"先打开文件菜单","confidence":1}""", "codex_point"),
            ("Codex image coordinates stay within normalized bounds", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[0,0,1000,1000],"confidence":0}""", "codex_point"),
            ("Codex completion carries no target coordinates", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","label":"已完成","confidence":1}""", "codex_point"),
            ("Codex not-found result carries no fabricated entry", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"not_found","confidence":0.2}""", "codex_point"),
            ("Codex ambiguous result carries no fabricated entry", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"ambiguous","confidence":0.3}""", "codex_point"),
            ("Codex wait defaults to a bounded long poll", """{"command":"codex_wait","sessionId":"s","afterEvent":0}""", "codex_wait"),
            ("Codex wait accepts explicit shortest timeout", """{"command":"codex_wait","sessionId":"s","afterEvent":1,"timeoutSeconds":1}""", "codex_wait"),
            ("Codex wait accepts largest event counter and timeout", """{"command":"codex_wait","sessionId":"s","afterEvent":9223372036854775807,"timeoutSeconds":25}""", "codex_wait") }) Accept(label, json, command);
        foreach (var (label, json) in new (string, string)[] {
            ("Codex read requires session", """{"command":"codex_read"}"""),
            ("Codex read rejects another command's fields even when null", """{"command":"codex_read","sessionId":"s","question":null}"""),
            ("Legacy status does not accept new nullable session field", """{"command":"status","sessionId":null}"""),
            ("Codex start requires explicit prior binding instead of a target field", """{"command":"codex_start","question":"Find","targetId":"aabbcc0011223344"}"""),
            ("Codex point requires snapshot", """{"command":"codex_point","sessionId":"s","action":"done","confidence":1}"""),
            ("Codex point requires confidence", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done"}"""),
            ("Codex point rejects null confidence", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","confidence":null}"""),
            ("Codex point rejects negative confidence", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","confidence":-0.1}"""),
            ("Codex point rejects excessive confidence", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","confidence":1.1}"""),
            ("Codex point rejects non-finite numeric overflow", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","confidence":1e999}"""),
            ("Codex point rejects numeric strings", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","confidence":"1"}"""),
            ("Codex point rejects unsupported action", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"click","confidence":1}"""),
            ("Codex point needs an actual candidate or image box", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","confidence":1}"""),
            ("Codex point rejects simultaneous candidate and image box", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","candidateId":"c","box":[1,2,3,4],"confidence":1}"""),
            ("Codex completion rejects stray candidate", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","candidateId":"c","confidence":1}"""),
            ("Codex ambiguous result rejects stray image box", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"ambiguous","box":[1,2,3,4],"confidence":1}"""),
            ("Codex image box needs four values", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[1,2,3],"confidence":1}"""),
            ("Codex image box rejects negative values", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[-1,2,3,4],"confidence":1}"""),
            ("Codex image box rejects values above normalized range", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[1,2,1001,4],"confidence":1}"""),
            ("Codex image box rejects non-finite overflow", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[1,2,1e999,4],"confidence":1}"""),
            ("Codex image box rejects string values", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"point","box":[1,"2",3,4],"confidence":1}"""),
            ("Codex point label must be visible", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","label":"  ","confidence":1}"""),
            ("Codex point label rejects control characters", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","label":"a\nb","confidence":1}"""),
            ("Codex point label rejects invisible format characters", """{"command":"codex_point","sessionId":"s","snapshotId":"p","action":"done","label":"\u200b","confidence":1}"""),
            ("Codex tokens reject path-like input", """{"command":"codex_read","sessionId":"../s"}"""),
            ("Codex tokens reject unicode", """{"command":"codex_read","sessionId":"会话"}"""),
            ("Codex tokens reject empty text", """{"command":"codex_read","sessionId":""}"""),
            ("Codex wait requires event counter", """{"command":"codex_wait","sessionId":"s"}"""),
            ("Codex wait rejects negative event counter", """{"command":"codex_wait","sessionId":"s","afterEvent":-1}"""),
            ("Codex wait rejects fractional event counter", """{"command":"codex_wait","sessionId":"s","afterEvent":1.5}"""),
            ("Codex wait rejects overflowing event counter", """{"command":"codex_wait","sessionId":"s","afterEvent":9223372036854775808}"""),
            ("Codex wait rejects zero timeout", """{"command":"codex_wait","sessionId":"s","afterEvent":0,"timeoutSeconds":0}"""),
            ("Codex wait rejects excessive timeout", """{"command":"codex_wait","sessionId":"s","afterEvent":0,"timeoutSeconds":26}"""),
            ("Codex wait rejects fractional timeout", """{"command":"codex_wait","sessionId":"s","afterEvent":0,"timeoutSeconds":1.5}"""),
            ("Codex wait rejects timeout strings", """{"command":"codex_wait","sessionId":"s","afterEvent":0,"timeoutSeconds":"1"}"""),
            ("Codex wait rejects duplicate event fields", """{"command":"codex_wait","sessionId":"s","afterEvent":0,"afterEvent":1}"""),
            ("Codex requests reject unknown fields", """{"command":"codex_read","sessionId":"s","extra":null}"""),
            ("Codex commands remain case sensitive", """{"command":"CODEX_READ","sessionId":"s"}""") }) Reject(label, json);
        Reject("Codex tokens have a hard size bound", JsonSerializer.Serialize(new { command = "codex_read", sessionId = new string('x', 97) }));
        Reject("Codex labels have a hard size bound", JsonSerializer.Serialize(new { command = "codex_point", sessionId = "s", snapshotId = "p", action = "done", label = new string('x', 161), confidence = 1 }));
        Accept("Codex tokens accept the maximum valid length", JsonSerializer.Serialize(new { command = "codex_read", sessionId = new string('x', 96) }), "codex_read");
        var parsed = PluginGuidePolicy.Parse("""{"command":"codex_point","sessionId":"session","snapshotId":"snapshot","action":"open_entry","box":[12.5,20,50,60],"label":"打开菜单","confidence":0.9}""");
        checks.Add(("Codex typed request preserves validated point fields", parsed.SessionId == "session" && parsed.SnapshotId == "snapshot" &&
            parsed.Action == "open_entry" && parsed.Box is [12.5, 20, 50, 60] && parsed.Label == "打开菜单" && parsed.Confidence == 0.9));
        checks.Add(("Codex wait deadline includes a bounded response margin", PluginGuidePolicy.RequestTimeout(new("codex_wait", TimeoutSeconds: 25)).TotalSeconds == 27 &&
            PluginGuidePolicy.RequestTimeout(new("codex_wait", TimeoutSeconds: 1)).TotalSeconds == 3));
        checks.Add(("Codex capture deadline stays within ten-second client deadline", PluginGuidePolicy.RequestTimeout(new("codex_read")).TotalSeconds == 9 &&
            PluginGuidePolicy.RequestTimeout(new("codex_start")).TotalSeconds == 9 && PluginGuidePolicy.RequestTimeout(new("codex_point")).TotalSeconds == 9));
        checks.Add(("Legacy quick requests keep four-second host deadline", PluginGuidePolicy.RequestTimeout(new("status")).TotalSeconds == 4 &&
            PluginGuidePolicy.RequestTimeout(new("clear")).TotalSeconds == 4));
        return checks;
    }
}
