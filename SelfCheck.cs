using System.IO;
using System.Text.Json;
using System.Windows;
using System.Runtime.InteropServices;

namespace ScreenGuide;

internal static class SelfCheck
{
    internal static int Run()
    {
        ControlOption[] controls = [new("c1-001", "设置", "Button", "ToolBar", true, false),
            new("c1-002", "编辑", "MenuItem", "MenuBar", true, true),
            new("c1-003", "查找", "MenuItem", "Menu", false, false),
            new("c1-004", "自动换行", "CheckBox", "", true, false)];
        var cases = new (string Label, string Json, string? Id, string? Action)[] {
            ("valid selection", "{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":0.99}", "c1-001", "point"),
            ("visible parent entry", "{\"action\":\"open_entry\",\"id\":\"c1-002\",\"confidence\":0.95}", "c1-002", "open_entry"),
            ("not found", "{\"action\":\"not_found\",\"id\":null,\"confidence\":0.95}", null, "not_found"),
            ("ambiguous", "{\"action\":\"ambiguous\",\"id\":null,\"confidence\":0.95}", null, "ambiguous"),
            ("invented ID", "{\"action\":\"point\",\"id\":\"c1-999\",\"confidence\":1}", null, null),
            ("ID from other snapshot", "{\"action\":\"point\",\"id\":\"c2-001\",\"confidence\":1}", null, null),
            ("disabled entry", "{\"action\":\"point\",\"id\":\"c1-003\",\"confidence\":1}", null, null),
            ("checkbox cannot open menu", "{\"action\":\"open_entry\",\"id\":\"c1-004\",\"confidence\":1}", null, null),
            ("coordinates rejected", "{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":1,\"x\":100}", null, null),
            ("duplicate fields", "{\"action\":\"point\",\"id\":\"c1-001\",\"id\":\"c1-002\",\"confidence\":1}", null, null),
            ("missing confidence", "{\"action\":\"point\",\"id\":\"c1-001\"}", null, null),
            ("low confidence", "{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":0.4}", null, null),
            ("impossible confidence", "{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":2}", null, null),
            ("wrong field type", "{\"action\":42,\"id\":\"c1-001\",\"confidence\":1}", null, null),
            ("multiple selection", "{\"action\":\"point\",\"id\":[\"c1-001\",\"c1-002\"],\"confidence\":1}", null, null),
            ("automatic action rejected", "{\"action\":\"click\",\"id\":\"c1-001\",\"confidence\":1}", null, null),
            ("refusal cannot carry ID", "{\"action\":\"not_found\",\"id\":\"c1-001\",\"confidence\":1}", null, null),
            ("non JSON rejected", "```json\n{}\n```", null, null)
        };
        var results = new List<object>(); int passed = 0;
        foreach (var c in cases)
        {
            var result = IntentMapper.Parse(c.Json, controls);
            var ok = result.Id == c.Id && (c.Action == null ? !result.CanPoint : result.Action == c.Action);
            if (ok) passed++; results.Add(new { c.Label, passed = ok });
        }
        var context = new SelectionContext("fixture", false, controls);
        var checks = new (string Label, bool Passed)[] {
            ("manual unique match", Locator.Manual(context, "设置").Id == "c1-001"),
            ("manual disabled match", !Locator.Manual(context, "查找").CanPoint),
            ("manual duplicate names", !Locator.Manual(new("fixture", false, [controls[0], controls[0] with { Id = "c1-005" }]), "设置").CanPoint),
            ("long labels excluded", Locator.SafeLabel(new string('x', 161)) == ""),
            ("multiline labels excluded", Locator.SafeLabel("line1\nline2") == ""),
            ("model context excludes geometry", !JsonSerializer.Serialize(context).Contains("Bounds") && !JsonSerializer.Serialize(context).Contains("Element"))
        };
        foreach (var c in checks) { if (c.Passed) passed++; results.Add(new { c.Label, passed = c.Passed }); }
        void Check(string label, bool ok) { if (ok) passed++; results.Add(new { Label = label, passed = ok }); }
        var bounds = new Rect(100, 100, 80, 30); var point = new Point(120, 110);
        var click = new PointerClickTracker();
        click.Down(1, bounds, point, 1000); Check("one press+release is one click", click.Up(point, 1100) == 1);
        Check("duplicate release ignored", click.Up(point, 1101) == null);
        click.Down(2, bounds, new(0, 0), 1200); Check("outside press ignored", click.Up(point, 1300) == null);
        click.Down(3, bounds, point, 1400); Check("outside release ignored", click.Up(new(200, 200), 1500) == null);
        click.Down(4, bounds, point, 1600); Check("drag inside wide control ignored", click.Up(new(145, 110), 1700) == null);
        click.Down(5, bounds, point, 1800); Check("long hold ignored", click.Up(point, 4000) == null);
        click.Down(6, bounds, point, 5000); click.Reset(); Check("cancelled press ignored", click.Up(point, 5100) == null);
        GuidanceFrame Frame(int token, string action = "open_entry") => new(token,
            new(1, bounds, null!, bounds, 1, [1], controls[1].Name, controls[1].Role), controls[1], action, GuideSession.Entries(context));
        var session = new GuideSession("查找在哪里？", 1, false); var frame = Frame(10);
        Check("first interaction remembered", session.Record(frame, "clicked") && session.Count == 1);
        Check("one frame cannot advance twice", !session.Record(frame, "clicked") && session.Count == 1);
        Check("last interacted entry cannot loop", session.RepeatsLast(controls[1]));
        session.Paused = true; Check("pause blocks pending continuation", !session.Record(Frame(11), "clicked"));
        session.Paused = false; Check("resumed guide preserves memory", session.Count == 1 && session.Record(Frame(11), "hover_revealed"));
        Check("hover never masquerades as click", session.Memory.Interactions[^1].Event == "hover_revealed");
        Check("new IDs alone are not an expansion", !GuideSession.HasNewEntries(frame.VisibleEntries, new("fixture", false, controls.Select(c => c with { Id = "new-" + c.Id }).ToArray())));
        Check("new readable item reveals expansion", GuideSession.HasNewEntries(frame.VisibleEntries, new("fixture", false, [.. controls, new("new-1", "查找下一个", "MenuItem", "Menu", true, false)])));
        Check("memory contains no stale element or geometry", !JsonSerializer.Serialize(session.Memory).Contains("Bounds") && !JsonSerializer.Serialize(session.Memory).Contains("RuntimeId") && !JsonSerializer.Serialize(session.Memory).Contains("c1-"));
        const string done = "{\"action\":\"done\",\"id\":null,\"confidence\":0.98}";
        Check("done without interaction rejected", IntentMapper.Parse(done, controls).Action != "done");
        Check("done after only hover rejected", IntentMapper.Parse(done, controls, session.Memory).Action != "done");
        var parentMemory = new GuideMemory([new(1, "编辑", "MenuItem", "MenuBar", "open_entry", "clicked")]);
        Check("done after only parent click rejected", IntentMapper.Parse(done, controls, parentMemory).Action != "done");
        var finalMemory = new GuideMemory([new(1, "设置", "Button", "ToolBar", "point", "clicked")]);
        Check("done allowed after clicked final entry", IntentMapper.Parse(done, controls, finalMemory).Action == "done");
        Check("done with ID rejected", IntentMapper.Parse(done.Replace("null", "\"c1-001\""), controls, finalMemory).Action != "done");
        Check("low confidence done rejected", IntentMapper.Parse(done.Replace("0.98", "0.3"), controls, finalMemory).Action != "done");
        Check("identical metadata cannot be arbitrarily chosen", IntentMapper.Parse("{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":1}", [controls[0], controls[0] with { Id = "c1-duplicate" }]).Action == "ambiguous");
        Check("submenu row cannot count as a final point", IntentMapper.Parse("{\"action\":\"point\",\"id\":\"c1-002\",\"confidence\":1}", controls).Action == "open_entry");
        while (session.Count < GuideSession.MaxInteractions) session.Record(Frame(100 + session.Count), "clicked");
        Check("guide has a finite interaction bound", !session.Record(Frame(999), "clicked") && session.Count == GuideSession.MaxInteractions);
        session.Complete = true; Check("completed guide cannot auto-advance", !session.Record(Frame(1000), "clicked"));
        Check("GUI thread info native layout", Marshal.SizeOf<Native.GUITHREADINFO>() == (IntPtr.Size == 8 ? 72 : 48));
        Check("ownerless popup matches active target menu", Native.MenuAssociation(0x10, true, 10, 10, 20, 20));
        Check("menu loop flag also accepted", Native.MenuAssociation(0x04, true, 10, 10, 20, 20));
        Check("popup of other process rejected", !Native.MenuAssociation(0x10, true, 10, 11, 20, 20));
        Check("popup of independent target rejected", !Native.MenuAssociation(0x10, false, 10, 10, 20, 20));
        Check("popup of unrelated thread rejected", !Native.MenuAssociation(0x10, true, 10, 10, 20, 21));
        Check("closed menu cannot retain association", !Native.MenuAssociation(0, true, 10, 10, 20, 20));
        Check("invalid thread cannot match", !Native.MenuAssociation(0x10, true, 10, 10, 0, 0));
        Check("invalid process cannot match", !Native.MenuAssociation(0x10, true, 0, 0, 20, 20));
        Check("tentative parent accepts explicit exploration", IntentMapper.Parse("{\"action\":\"open_entry\",\"id\":\"c1-002\",\"confidence\":0.60}", controls).Action == "open_entry");
        Check("unreliable parent still refused", !IntentMapper.Parse("{\"action\":\"open_entry\",\"id\":\"c1-002\",\"confidence\":0.59}", controls).CanPoint);
        Check("final target keeps strict threshold", !IntentMapper.Parse("{\"action\":\"point\",\"id\":\"c1-001\",\"confidence\":0.72}", controls).CanPoint);
        const string imagePoint = "{\"action\":\"point\",\"label\":\"Find\",\"box\":[100,200,300,300],\"confidence\":0.98}";
        Check("image selects only explicit normalized box", VisionMapper.Parse(imagePoint).CanPoint);
        Check("image lower confidence refused", !VisionMapper.Parse(imagePoint.Replace("0.98", "0.89")).CanPoint);
        Check("image coordinates outside range refused", !VisionMapper.Parse(imagePoint.Replace("300,300", "1001,300")).CanPoint);
        Check("image reversed box refused", !VisionMapper.Parse(imagePoint.Replace("100,200,300,300", "300,200,100,300")).CanPoint);
        Check("image whole-window box refused", !VisionMapper.Parse(imagePoint.Replace("100,200,300,300", "0,0,1000,1000")).CanPoint);
        Check("image automatic click action refused", !VisionMapper.Parse(imagePoint.Replace("point", "click")).CanPoint);
        Check("image extra field refused", !VisionMapper.Parse(imagePoint.Replace("0.98}", "0.98,\"click\":true}")).CanPoint);
        Check("image duplicate field refused", !VisionMapper.Parse(imagePoint.Replace("\"point\",", "\"point\",\"action\":\"point\",")).CanPoint);
        Check("image multiple boxes refused", !VisionMapper.Parse(imagePoint.Replace("[100,200,300,300]", "[[100,200,300,300],[100,200,300,300]]")).CanPoint);
        Check("image blank label refused", !VisionMapper.Parse(imagePoint.Replace("Find", " ")).CanPoint);
        Check("image control characters refused", !VisionMapper.Parse(imagePoint.Replace("Find", "Find\\nNow")).CanPoint);
        Check("image negative score refused", !VisionMapper.Parse(imagePoint.Replace("0.98", "-1")).CanPoint);
        Check("image non-JSON refused", !VisionMapper.Parse("```json\n" + imagePoint + "\n```").CanPoint);
        const string imageDone = "{\"action\":\"done\",\"label\":null,\"box\":null,\"confidence\":0.98}";
        Check("image done without click refused", VisionMapper.Parse(imageDone).Action != "done");
        Check("image done after parent refused", VisionMapper.Parse(imageDone, parentMemory).Action != "done");
        Check("image done after final click allowed", VisionMapper.Parse(imageDone, finalMemory).Action == "done");
        var imageSession = new GuideSession("New machine", 1, false, GuideMode.DirectImage);
        Check("image session remembers pixel change without claiming expansion", imageSession.Record(Frame(9000), "hover_changed") && imageSession.Memory.Interactions[^1].Event == "hover_changed");
        Check("image hover does not count as final click", VisionMapper.Parse(imageDone, imageSession.Memory).Action != "done");
        Check("UIA session cannot fabricate image event", !new GuideSession("Find", 1, false).Record(Frame(9001), "hover_changed"));
        Check("manual mode cannot enable images", !new GuideSession("Find", 1, true, GuideMode.DirectImage).Images);
        foreach (var failure in Enum.GetValues<ControlReadFailure>())
        {
            var controlsOnly = new GuideSession("Find", 1, false);
            Check("controls-only cannot send image after " + failure, !controlsOnly.TryActivateImageFallback(failure) && !controlsOnly.Images);
            var fallbackSession = new GuideSession("Find", 1, false, GuideMode.ImageOnControlFailure);
            Check("conditional mode starts without image before " + failure, !fallbackSession.Images && fallbackSession.ImageFallbackReason == null);
            Check("conditional mode switches after " + failure, fallbackSession.TryActivateImageFallback(failure) && fallbackSession.Images && fallbackSession.ImageFallbackReason == failure);
        }
        var retainedSession = new GuideSession("怎么创建虚拟机", 123, false, GuideMode.ImageOnControlFailure);
        retainedSession.Record(Frame(9100), "clicked");
        var retainedMemory = JsonSerializer.Serialize(retainedSession.Memory);
        retainedSession.TryActivateImageFallback(ControlReadFailure.MenuNotUpdated);
        Check("fallback keeps original goal, target and recorded click", retainedSession.Question == "怎么创建虚拟机" && retainedSession.Window == 123 && retainedSession.Count == 1 && JsonSerializer.Serialize(retainedSession.Memory) == retainedMemory);
        Check("fallback does not count as a click or hover", retainedSession.Memory.Interactions[^1].Event == "clicked" && retainedSession.Count == 1);
        Check("fallback cannot activate repeatedly", !retainedSession.TryActivateImageFallback(ControlReadFailure.ReadError) && retainedSession.ImageFallbackReason == ControlReadFailure.MenuNotUpdated);
        Check("source switch cannot repeat the last visible entry", retainedSession.RepeatsLast(controls[1] with { Role = "ImageEntry", Context = "当前截图" }));
        retainedSession.Paused = true;
        Check("pause preserves image fallback until explicit reset", retainedSession.Images && retainedSession.Count == 1);
        Check("paused fallback cannot manufacture progress", !retainedSession.Record(Frame(9101), "hover_changed"));
        Check("new guide resets conditional image state", !new GuideSession("Next", 123, false, GuideMode.ImageOnControlFailure).Images);
        var pausedRoute = new GuideSession("Find", 1, false, GuideMode.ImageOnControlFailure) { Paused = true };
        Check("cancelled or paused guide cannot auto-switch", !pausedRoute.TryActivateImageFallback(ControlReadFailure.ReadTimeout) && !pausedRoute.Images);
        var completedRoute = new GuideSession("Find", 1, false, GuideMode.ImageOnControlFailure) { Complete = true };
        Check("completed guide cannot auto-switch", !completedRoute.TryActivateImageFallback(ControlReadFailure.ReadError));
        var manualRoute = new GuideSession("Find", 1, true, GuideMode.ImageOnControlFailure);
        Check("manual debugging cannot auto-send images", !manualRoute.TryActivateImageFallback(ControlReadFailure.EmptyControls) && !manualRoute.Images && manualRoute.Mode == GuideMode.Controls);
        Check("unknown guide mode fails closed", !new GuideSession("Find", 1, false, (GuideMode)999).Images);
        Check("unknown failure cannot permit images", !new GuideSession("Find", 1, false, GuideMode.ImageOnControlFailure).TryActivateImageFallback((ControlReadFailure)999));
        Check("direct image mode does not need a control failure", new GuideSession("Find", 1, false, GuideMode.DirectImage).Images);
        Check("parent entry expects a readable next layer", (Frame(9200) with { Option = controls[0] }).ExpectsChild);
        Check("hovering a final entry is not a missing submenu", !(Frame(9201, "point") with { Option = controls[0] }).ExpectsChild);
        var testImage = new PixelSnapshot(1, new(-1000, 100, 1000, 500), new(-1000, 100, 1000, 500), 1000, 500, new byte[1000 * 500 * 4], []);
        var mapped = ScreenCapture.MapBox(testImage, [100, 350, 300, 400]);
        Check("normalized coordinates preserve negative monitor origin", mapped == new Rect(-900, 200, 200, 50));
        Check("small image estimate refused", ScreenCapture.MapBox(testImage, [1, 1, 2, 2]).IsEmpty);
        Check("masked region cannot be highlighted", ScreenCapture.MapBox(testImage with { Masks = [mapped] }, [100, 350, 300, 400]).IsEmpty);
        Check("mask outside estimate allowed", !ScreenCapture.MapBox(testImage with { Masks = [new(-1000, 100, 20, 20)] }, [100, 350, 300, 400]).IsEmpty);
        Check("gray letterbox cannot be pointed at", ScreenCapture.MapBox(testImage, [100, 100, 300, 150]).IsEmpty);
        Check("wide image letterbox preserves aspect", ScreenCapture.ImageViewport(1000, 500) == new Rect(0, 250, 1000, 500));
        Check("tall image letterbox preserves aspect", ScreenCapture.ImageViewport(500, 1000) == new Rect(250, 0, 500, 1000));
        Check("square image has no letterbox", ScreenCapture.ImageViewport(900, 900) == new Rect(0, 0, 1000, 1000));
        var flat = new byte[24 * 16 * 4]; var edges = new byte[flat.Length];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 24; x++)
        { int i = (y * 24 + x) * 4; edges[i] = edges[i + 1] = edges[i + 2] = (byte)(((x / 4 + y / 4) % 2) * 255); }
        var inverse = edges.Select((v, i) => i % 4 == 3 ? v : (byte)(255 - v)).ToArray();
        Check("same image patch remains valid", ScreenCapture.PatchMatches(edges, edges, 24, 16));
        Check("normal hover inversion retains edge identity", ScreenCapture.PatchMatches(edges, inverse, 24, 16));
        Check("disappeared image content invalidates estimate", !ScreenCapture.PatchMatches(edges, flat, 24, 16));
        Check("blank patch has no reliable identity", !ScreenCapture.PatchMatches(flat, flat, 24, 16));
        Check("mismatched patch dimensions refused", !ScreenCapture.PatchMatches(edges, flat, 23, 16));
        var unchanged = testImage with { Pixels = (byte[])testImage.Pixels.Clone() };
        Check("unchanged screenshot cannot auto-advance", !ScreenCapture.HasChanged(testImage, unchanged, mapped));
        var changedPixels = (byte[])testImage.Pixels.Clone(); ScreenCapture.PaintMask(changedPixels, 1000, testImage.ScreenBounds, new(-500, 200, 300, 200));
        var changedImage = testImage with { Pixels = changedPixels };
        Check("new pixels outside entry trigger a read", ScreenCapture.HasChanged(testImage, changedImage, mapped));
        Check("hover background inside entry alone cannot advance", !ScreenCapture.HasChanged(testImage, changedImage, new(-500, 200, 300, 200)));
        Check("masked pixels do not trigger continuation", !ScreenCapture.HasChanged(testImage, changedImage with { Masks = [new(-500, 200, 300, 200)] }, mapped));
        Check("outside-region masks preserve area", Math.Abs(ScreenCapture.Outside(new(0, 0, 100, 100), new(20, 20, 60, 60)).Sum(r => r.Width * r.Height) - 6400) < 0.01);
        bool RejectsClip(float[] samples) { try { SpeechInput.ValidateClip(samples); return false; } catch (InvalidOperationException) { return true; } }
        bool RejectsTranscript(string? text) { try { SpeechInput.CleanTranscript(text); return false; } catch (InvalidOperationException) { return true; } }
        Check("empty recording cannot become a question", RejectsClip([]));
        Check("very short audio is refused", RejectsClip(new float[1000]));
        Check("silent recording cannot become a question", RejectsClip(new float[16000]));
        Check("oversized audio is refused", RejectsClip(new float[16000 * 30 + 1]));
        var invalidAudio = new float[16000]; invalidAudio[0] = float.NaN;
        Check("non-finite audio is refused", RejectsClip(invalidAudio));
        invalidAudio[0] = 2;
        Check("out-of-range audio is refused", RejectsClip(invalidAudio));
        Check("missing transcript cannot replace question", RejectsTranscript(null));
        Check("metadata-only transcript cannot replace question", RejectsTranscript("<|zh|><|NEUTRAL|><|Speech|>"));
        Check("long transcript is refused without truncating", RejectsTranscript(new string('字', 501)));
        Check("control characters cannot enter question", RejectsTranscript("查找\0在哪里"));
        Check("Chinese and software name remain unchanged", SpeechInput.CleanTranscript("<|zh|><|NEUTRAL|>VS Code 的颜色主题在哪改？") == "VS Code 的颜色主题在哪改？");
        using (var inactiveSpeech = new SpeechInput())
        {
            Check("speech service starts without opening microphone", !inactiveSpeech.IsRecording && !inactiveSpeech.IsBusy);
            inactiveSpeech.Cancel(); inactiveSpeech.Cancel();
            Check("idle cancellation is repeatable", !inactiveSpeech.IsRecording && !inactiveSpeech.IsBusy);
        }
        Check("engine choices have unique identities", SpeechEngines.All.Select(SpeechEngines.Id).Distinct().Count() == 3);
        Check("unknown engine text is refused", !SpeechEngines.TryParse("whisper", out _) && !SpeechEngines.TryParse("automatic", out _));
        Check("missing engine selection is refused", !SpeechEngines.TryParse(null, out _));
        Check("CLI engine selection resolves without fuzzy guessing", SpeechEngines.TryParse(" ZIPFORMER ", out var selectedSpeech) && selectedSpeech == SpeechEngine.Zipformer);
        bool RejectsEngine(SpeechEngine engine) { try { SpeechEngines.CreateConfig(engine); return false; } catch (ArgumentOutOfRangeException) { return true; } }
        Check("invalid engine cannot silently load a default", RejectsEngine((SpeechEngine)999));
        var senseConfig = SpeechEngines.CreateConfig(SpeechEngine.SenseVoice);
        var paraConfig = SpeechEngines.CreateConfig(SpeechEngine.Paraformer);
        var zipConfig = SpeechEngines.CreateConfig(SpeechEngine.Zipformer);
        Check("engine models do not share an accidental directory", SpeechEngines.All.Select(SpeechEngines.ModelDirectory).Distinct().Count() == 3);
        Check("SenseVoice selection cannot load Paraformer", !string.IsNullOrEmpty(senseConfig.ModelConfig.SenseVoice.Model) && string.IsNullOrEmpty(senseConfig.ModelConfig.Paraformer.Model) && string.IsNullOrEmpty(senseConfig.ModelConfig.Transducer.Encoder));
        Check("Paraformer selection cannot load SenseVoice", !string.IsNullOrEmpty(paraConfig.ModelConfig.Paraformer.Model) && string.IsNullOrEmpty(paraConfig.ModelConfig.SenseVoice.Model) && string.IsNullOrEmpty(paraConfig.ModelConfig.Transducer.Encoder));
        Check("Zipformer has all three transducer components", !string.IsNullOrEmpty(zipConfig.ModelConfig.Transducer.Encoder) && !string.IsNullOrEmpty(zipConfig.ModelConfig.Transducer.Decoder) && !string.IsNullOrEmpty(zipConfig.ModelConfig.Transducer.Joiner));
        Check("each engine uses its own vocabulary", new[] { senseConfig.ModelConfig.Tokens, paraConfig.ModelConfig.Tokens, zipConfig.ModelConfig.Tokens }.Distinct().Count() == 3);
        Check("all speech engines use local CPU", new[] { senseConfig, paraConfig, zipConfig }.All(c => c.ModelConfig.Provider == "cpu" && c.FeatConfig.SampleRate == SpeechInput.SampleRate));
        Check("homophone error is not counted as matching text", !SpeechCheck.ExpectedTextMatches("开饭时间早上9点至下午5点。"));
        Check("official expected phrase is accepted", SpeechCheck.ExpectedTextMatches("开放时间早上九点至下午五点"));
        foreach (var shellCheck in ShellCheck.Run()) Check(shellCheck.Label, shellCheck.Passed);
        foreach (var endpointCheck in SpeechEndpointCheck.Checks()) Check(endpointCheck.Label, endpointCheck.Passed);
        foreach (var voiceCheck in VoiceChatStateCheck.Checks()) Check(voiceCheck.Label, voiceCheck.Passed);
        foreach (var locatorCheck in LocatorPolicyCheck.Checks()) Check(locatorCheck.Label, locatorCheck.Passed);
        foreach (var selectionCheck in IntentMapperCheck.Checks()) Check(selectionCheck.Label, selectionCheck.Passed);
        foreach (var fallbackCheck in ImageFallbackPolicyCheck.Checks()) Check(fallbackCheck.Label, fallbackCheck.Passed);
        foreach (var profileCheck in ModelProfilesCheck.Checks()) Check(profileCheck.Label, profileCheck.Passed);
        foreach (var settingsCheck in AppSettingsCheck.Checks()) Check(settingsCheck.Label, settingsCheck.Passed);
        foreach (var captureCheck in CaptureGeometryCheck.Checks()) Check(captureCheck.Label, captureCheck.Passed);
        foreach (var hybridCheck in HybridGuidePolicyCheck.Checks()) Check(hybridCheck.Label, hybridCheck.Passed);
        foreach (var hybridDecisionCheck in HybridDecisionCheck.Checks()) Check(hybridDecisionCheck.Label, hybridDecisionCheck.Passed);
        foreach (var preferenceUiCheck in MainWindow.PreferenceUiChecks()) Check(preferenceUiCheck.Label, preferenceUiCheck.Passed);
        foreach (var pluginCheck in PluginGuidePolicyCheck.Checks()) Check(pluginCheck.Label, pluginCheck.Passed);
        foreach (var stateCheck in CodexGuideStateCheck.RunChecksAsync().GetAwaiter().GetResult()) Check(stateCheck.Label, stateCheck.Passed);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-check.json"), JsonSerializer.Serialize(new { scope = "Bounded Codex guide event journal, model profile persistence with synthetic keys, plugin input policy, selection/protocol failures, structural UIA coverage, authorized outcome-image fallback, image geometry, interactions, audio/conversation and edge dock; no live desktop, microphone, screenshot or model request", passed, total = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
        return passed == results.Count ? 0 : 1;
    }
}
