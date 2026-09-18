using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IlMatto.Desktop;
using IlMatto.Desktop.Controls;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;

internal static class Program
{
    private static string Root = "";
    private static int Passed;
    private static string? Filter;

    [STAThread]
    private static int Main(string[] args)
    {
        Filter = args.FirstOrDefault();
        Root = Path.Combine(Path.GetTempPath(), "ilmatto-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Check("save requests coalesce and writes remain ordered", WriterOrdering);
                await Check("failed saves retain the latest dirty snapshot", WriterRetry);
                await Check("snapshots detach mutable state and old JSON still loads", SnapshotIsolation);
                await Check("autosave omits transient and empty timeline rows", PersistedMessageFilter);
                await Check("streamed single/mixed segments preserve text and sharing", TextSharing);
                await Check("only active task runtimes publish elapsed updates", ActiveRuntimes);
                await Check("background event coalescing preserves boundaries", EventBuffer);
                await Check("background/foreground tails survive shutdown and restart", ShutdownReplay);
                await Check("switching sessions does not duplicate replayed messages", SwitchReplay);
                await Check("background tools/errors remain in their own conversation", BackgroundTools);
                await Check("saving and appending retain the allocation improvements", AllocationBudgets);
                await Check("image previews preserve aspect, alpha, DPI detail and file access", ImagePreviews);
                await Check("preview controls load, defer and release their bitmap", PreviewLifecycle);
                await Check("short emoji bubbles remain content-sized", () => TimelineRegressionChecks.EmojiBubbleWidth());
                await Check("timeline geometry survives materialization and scrolling", () => TimelineRegressionChecks.Geometry(Root));
                await Check("timeline index retains measured streaming and long-row heights", TimelineRegressionChecks.IndexConsistency);
            }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            Console.Error.WriteLine($"Fixtures retained at {Root}");
            return 1;
        }
        // This directory is created above with a unique name, never user data.
        Directory.Delete(Root, true);
        Console.WriteLine($"PASS: {Passed} desktop regression checks");
        return 0;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        if (Filter is not null && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        await test();
        Passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WriterOrdering()
    {
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = new List<int>();
        var value = 1;
        var captures = 0;
        using var writer = new ManagerConversationWriter(Dispatcher.CurrentDispatcher, () =>
        {
            Assert(Environment.CurrentManagedThreadId == dispatcherThread, "Snapshot captured off UI thread");
            captures++;
            var snapshot = value;
            return () =>
            {
                Assert(Environment.CurrentManagedThreadId != dispatcherThread, "Write blocked the UI thread");
                if (snapshot == 1)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Write barrier timed out");
                }
                writes.Add(snapshot);
            };
        });
        for (var i = 0; i < 100; i++) writer.RequestSave();
        var flush = writer.FlushAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(captures == 1, "Requests were not coalesced");
        value = 2;
        for (var i = 0; i < 100; i++) writer.RequestSave();
        var joiningFlush = writer.FlushAsync();
        release.Set();
        await Task.WhenAll(flush, joiningFlush);
        Assert(captures == 2 && writes.SequenceEqual(new[] { 1, 2 }), "Stale snapshot overwrote the latest state");
    }

    private static async Task WriterRetry()
    {
        var fail = true;
        var persisted = 0;
        var value = 1;
        using var writer = new ManagerConversationWriter(Dispatcher.CurrentDispatcher, () =>
        {
            var snapshot = value;
            return () => { if (fail) throw new IOException("synthetic failure"); persisted = snapshot; };
        });
        writer.RequestSave();
        try { await writer.FlushAsync(); throw new InvalidOperationException("Expected save failure"); }
        catch (IOException) { }
        fail = false;
        value = 2;
        await writer.FlushAsync();
        Assert(persisted == 2, "A failed save lost dirty state");
    }

    private static async Task SnapshotIsolation()
    {
        var file = Path.Combine(Root, "snapshot.json");
        var conversation = Conversation("snapshot");
        var entry = new ManagerChatEntry("Agent", "antigravity", "原文\n**粗体** 🙂");
        var result = new ManagerCodeResult { SummaryForUser = "原结果", FilesChanged = new() { new() { Path = "original.cs", Additions = 1 } } };
        entry.CodeResult = result;
        conversation.Messages.Add(entry);
        var write = ManagerConversationStore.CreateSaveOperation(new[] { conversation }, file);
        entry.Append("追加内容");
        conversation.MainAgent!.Model = "changed";
        result.FilesChanged[0].Path = "changed.cs";
        conversation.Messages.Clear();
        await Task.Run(write);
        var loaded = ManagerConversationStore.Load(file).Single();
        Assert(loaded.Messages.Single().Text == "原文\n**粗体** 🙂", "Snapshot retained a mutable message");
        Assert(loaded.MainAgent!.Model == "original", "Snapshot retained a mutable binding");
        Assert(loaded.Messages[0].CodeResult!.FilesChanged[0].Path == "original.cs", "Snapshot retained a nested mutable result");
        Assert(ReferenceEquals(loaded.Messages[0].Text, loaded.Messages[0].Segments[0].Text), "Load duplicated identical text");
        var firstBytes = File.ReadAllBytes(file);
        await Task.Run(write);
        Assert(firstBytes.AsSpan().SequenceEqual(File.ReadAllBytes(file)), "Captured snapshot changed between writes");

        var legacyFile = Path.Combine(Root, "legacy.json");
        await File.WriteAllTextAsync(legacyFile, """
            [{"SessionId":"legacy","Title":"旧记录","Messages":[{"Role":"Agent","Source":"antigravity","Text":"旧正文","ThinkingText":"旧思路","IsThinking":true}]}]
            """);
        var legacy = ManagerConversationStore.Load(legacyFile).Single().Messages.Single();
        Assert(legacy.Text == "旧正文" && !legacy.IsThinking && legacy.Segments.Any(s => s.IsText), "Legacy migration changed");
        var legacyJson = await File.ReadAllTextAsync(legacyFile);
        foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode, System.Text.Encoding.BigEndianUnicode, System.Text.Encoding.UTF32 })
        {
            await File.WriteAllTextAsync(legacyFile, legacyJson, encoding);
            Assert(ManagerConversationStore.Load(legacyFile).Single().Messages.Single().Text == "旧正文", $"Legacy BOM encoding failed: {encoding.WebName}");
        }
    }

    private static Task TextSharing()
    {
        var entry = new ManagerChatEntry("Agent", "antigravity", "");
        foreach (var chunk in new[] { "开头", "🙂", "\n代码" }) entry.Append(chunk);
        Assert(entry.Text == "开头🙂\n代码" && ReferenceEquals(entry.Text, entry.Segments[0].Text), "Single-segment sharing failed");
        entry.AppendOperation(new ProcessItem("工具", "run_command", "完成", "output"));
        entry.Append("结尾");
        entry.Append("继续");
        Assert(entry.Text == "开头🙂\n代码结尾继续", "Combined text changed");
        Assert(entry.Segments.Count == 3 && entry.Segments[0].Text == "开头🙂\n代码" && entry.Segments[2].Text == "结尾继续", "Mixed segment order changed");
        return Task.CompletedTask;
    }

    private static Task PersistedMessageFilter()
    {
        var conversation = Conversation("persist-filter");
        conversation.Messages.Add(new ManagerChatEntry("你", "user", "带图🙂", attachments: new[]
        {
            new ManagerImageAttachment { Path = Path.Combine(Root, "attachment.png"), DisplayName = "attachment.png" }
        }));
        conversation.Messages.Add(new ManagerChatEntry("Agent", "antigravity", ""));
        var pending = new ManagerChatEntry("Agent", "antigravity", "", isPendingAgent: true);
        pending.Runtime = new TaskRuntimeInfo(null, null, DateTimeOffset.UtcNow, "responding");
        Assert(pending.IsPendingAgent && !pending.HasMessageBody, "Pending Agent row exposed a message body");
        pending.SetPendingAgent(false);
        Assert(!pending.IsPendingAgent, "Pending Agent row could not transition to streamed content");
        pending.SetPendingAgent(true);
        conversation.Messages.Add(pending);
        conversation.Messages.Add(new ManagerChatEntry("Agent", "antigravity", "正在读取图片…", isTransientStatus: true));
        conversation.Messages.Add(new ManagerChatEntry("Agent", "antigravity", "正常回复"));

        var path = Path.Combine(Root, "persist-filter.json");
        // Use the internal overload so the test never touches the user's
        // normal LocalAppData conversation file.
        ManagerConversationStore.CreateSaveOperation(new[] { conversation }, path)();
        var loaded = ManagerConversationStore.Load(path).Single();
        Assert(loaded.Messages.Count == 2, "Transient or empty timeline rows were persisted");
        Assert(loaded.Messages[0].Attachments.Count == 1 && loaded.Messages[0].Text == "带图🙂", "Image+emoji message was filtered or changed");
        Assert(loaded.Messages[1].Text == "正常回复", "Normal assistant message was filtered");
        return Task.CompletedTask;
    }

    private static Task ActiveRuntimes()
    {
        using var tracker = new ActiveTaskRuntimes();
        var notifications = 0;
        var active = new TaskRuntimeInfo("active", null, DateTimeOffset.UtcNow);
        active.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TaskRuntimeInfo.DisplayLabel)) notifications++; };
        for (var i = 0; i < 10000; i++) tracker.Track(new TaskRuntimeInfo(i.ToString(), null, DateTimeOffset.UtcNow, "completed"));
        tracker.Track(active);
        tracker.Track(active);
        tracker.Refresh();
        Assert(tracker.Count == 1 && notifications == 1, "Inactive history was tracked or runtime refreshed twice");
        active.Mark("completed", DateTimeOffset.UtcNow, 1200);
        notifications = 0;
        tracker.Refresh();
        Assert(tracker.Count == 0 && notifications == 0 && active.DisplayLabel == "耗时 00:01", "Completed runtime still refreshed");
        return Task.CompletedTask;
    }

    private static Task EventBuffer()
    {
        var buffer = new ManagerEventBuffer();
        for (var i = 0; i < 1000; i++) buffer.Add(Delta("a", "字"));
        buffer.Add(new ManagerHostEvent { Type = "manager_tool_status", SessionId = "a", Text = "工具" });
        buffer.Add(Delta("a", "尾"));
        buffer.Add(new ManagerHostEvent { Type = "manager_completed", SessionId = "a" });
        var events = buffer.Drain();
        Assert(events.Count == 4 && events[0].Text == new string('字', 1000), "Deltas were not combined losslessly");
        Assert(events[1].Type == "manager_tool_status" && events[2].Text == "尾" && events[3].Type == "manager_completed" && buffer.Count == 0, "Event boundaries changed");
        return Task.CompletedTask;
    }

    private static async Task ShutdownReplay()
    {
        var a = Conversation("a");
        var b = Conversation("b");
        var file = Path.Combine(Root, "shutdown.json");
        ManagerViewModel? vm = null;
        vm = new ManagerViewModel(new[] { a, b }, () => ManagerConversationStore.CreateSaveOperation(vm!.Conversations, file));
        var prefix = new string('前', 1000) + "🙂";
        vm.HandleHostEvent(Delta("a", prefix));
        vm.HandleHostEvent(new ManagerHostEvent { Type = "manager_state", SessionId = "a", State = "responding", TurnId = "turn-a", StartedAt = DateTimeOffset.UtcNow.ToString("O") });
        vm.SelectedConversation = b;
        vm.RestoreSessionUiState(b);
        for (var i = 0; i < 1000; i++) vm.HandleHostEvent(Delta("a", "后"));
        vm.HandleHostEvent(new ManagerHostEvent { Type = "manager_completed", SessionId = "a", Final = true, CompletedAt = DateTimeOffset.UtcNow.ToString("O"), DurationMs = 1500, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2).ToString("O"), TurnId = "turn-a" });
        vm.HandleHostEvent(Delta("b", "B 的未显示尾部" + new string('乙', 4000)));
        await vm.DisposeAsync();
        var loaded = ManagerConversationStore.Load(file);
        Assert(loaded.Single(c => c.SessionId == "a").Messages.Where(m => m.Source == "antigravity").Select(m => m.Text).Aggregate("", (x, y) => x + y) == prefix + new string('后', 1000), "Background shutdown lost/duplicated text");
        Assert(loaded.Single(c => c.SessionId == "b").Messages.Single(m => m.Source == "antigravity").Text == "B 的未显示尾部" + new string('乙', 4000), "Foreground shutdown lost pending text");
        Assert(loaded.SelectMany(c => c.Messages).All(m => m.Runtime?.IsActive != true), "Restart resurrected an active runtime");
    }

    private static async Task SwitchReplay()
    {
        var a = Conversation("switch-a");
        var b = Conversation("switch-b");
        var file = Path.Combine(Root, "switch.json");
        ManagerViewModel? vm = null;
        vm = new ManagerViewModel(new[] { a, b }, () => ManagerConversationStore.CreateSaveOperation(vm!.Conversations, file));
        vm.HandleHostEvent(Delta(a.SessionId, "首段"));
        vm.SelectedConversation = b;
        vm.RestoreSessionUiState(b);
        vm.HandleHostEvent(Delta(a.SessionId, "后台"));
        vm.HandleHostEvent(new ManagerHostEvent { Type = "manager_completed", SessionId = a.SessionId, Final = true });
        vm.SelectedConversation = a;
        vm.RestoreSessionUiState(a);
        vm.ReplayBufferedEvents(a);
        vm.ReplayBufferedEvents(a);
        await vm.DisposeAsync();
        Assert(a.Messages.Single(m => m.Source == "antigravity").Text == "首段后台", "Switch replay duplicated or reordered messages");
        Assert(b.Messages.Count == 0, "Background reply leaked into selected conversation");
    }

    private static ManagerConversationItem Conversation(string id) => new(id, id)
    {
        MainAgent = new ManagerMainAgentBinding { Model = "original", Transport = "cli" },
        CodingAgent = new ManagerCodingAgentBinding(),
    };

    private static ManagerHostEvent Delta(string id, string text) => new() { Type = "manager_delta", SessionId = id, Source = "antigravity", Text = text };

    private static async Task BackgroundTools()
    {
        var a = Conversation("tool-a");
        var b = Conversation("tool-b");
        ManagerViewModel? vm = null;
        vm = new ManagerViewModel(new[] { a, b }, () => ManagerConversationStore.CreateSaveOperation(vm!.Conversations, Path.Combine(Root, "tools.json")));
        vm.SelectedConversation = b;
        vm.RestoreSessionUiState(b);
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_delta", SessionId = a.SessionId, Text = "工具前", Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_tool_started", SessionId = a.SessionId, Tool = "run_command", CallId = "call-a", Command = "echo test", Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_tool_output", SessionId = a.SessionId, Text = "test", CallId = "call-a", Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_tool_completed", SessionId = a.SessionId, CallId = "call-a", Ok = true, Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_delta", SessionId = a.SessionId, Text = "工具后", Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "coding_completed", SessionId = a.SessionId, Status = "completed", Provider = "pi", TaskId = "task-a" });
        vm.HandleHostEvent(new ManagerHostEvent { Type = "manager_error", SessionId = a.SessionId, Code = "synthetic", Message = "后台错误" });
        await vm.DisposeAsync();
        var coding = a.Messages.Single(m => m.Source == "pi");
        Assert(coding.Text == "工具前工具后", "Coding text was lost across a tool boundary");
        Assert(coding.Segments.Count == 3 && coding.Segments[0].Text == "工具前" && coding.Segments[2].Text == "工具后", "Tool/text order changed");
        Assert(coding.FindOperation("call-a")?.Details == "test", "Tool output changed");
        Assert(a.Messages.Any(m => m.Text.Contains("后台错误")) && b.Messages.Count == 0, "Background system message leaked into the selected session");
    }

    private static Task AllocationBudgets()
    {
        var conversations = Enumerable.Range(0, 20).Select(c => Conversation($"perf-{c}")).ToList();
        foreach (var conversation in conversations)
            for (var m = 0; m < 50; m++) conversation.Messages.Add(new ManagerChatEntry("Agent", "antigravity", new string('测', 1024)));
        var path = Path.Combine(Root, "performance.json");
        ManagerConversationStore.CreateSaveOperation(conversations, path)();
        var start = GC.GetAllocatedBytesForCurrentThread();
        ManagerConversationStore.CreateSaveOperation(conversations, path)();
        var saveBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(saveBytes < 3 * 1024 * 1024, "Save allocated an entire JSON string again");
        var entry = new ManagerChatEntry("Agent", "antigravity", "");
        var chunk = new string('测', 96);
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < 64000; n += 96) entry.Append(chunk[..Math.Min(96, 64000 - n)]);
        var appendBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(appendBytes < 50 * 1024 * 1024, "Single text segment was copied twice per append");
        Console.WriteLine($"  Save 1000 messages: {saveBytes / 1048576d:F3} MiB allocated; Append 64000 chars: {appendBytes / 1048576d:F2} MiB allocated");
        return Task.CompletedTask;
    }

    private static Task ImagePreviews()
    {
        foreach (var dimensions in new[] { (4000, 3000), (120, 4000), (64, 32) })
        {
            var (width, height) = dimensions;
            var path = Path.Combine(Root, $"image-{width}-{height}.png");
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    pixels[offset] = (byte)(x * 255 / width);
                    pixels[offset + 1] = (byte)(y * 255 / height);
                    pixels[offset + 2] = 160;
                    pixels[offset + 3] = (byte)(x < width / 2 ? 128 : 255);
                }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var output = File.Create(path)) encoder.Save(output);
            var originalBytes = File.ReadAllBytes(path);
            foreach (var dpi in new[] { 1.0, 1.5, 2.0, 3.0 })
            {
                var preview = ImagePreviewLoader.Load(path);
                Assert(preview.IsFrozen, "Preview was not frozen");
                Assert(preview.PixelWidth == width && preview.PixelHeight == height, "Preview changed original resolution");
                var baseline = Render(source, dpi);
                var thumbnail = Render(preview, dpi);
                var error = baseline.Zip(thumbnail, (a, b) => Math.Abs(a - b)).Average();
                Console.WriteLine($"  preview {width}x{height} at {dpi * 100}%: {preview.PixelWidth}x{preview.PixelHeight}, mean channel error {error:F3}/255");
                Assert(error < 0.01, "Preview diverged from the original at display size");
            }
            Assert(originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Preview modified the source image");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1600, 1000));
            for (var line = 0; line < 12; line++)
            {
                var text = new FormattedText($"IlMatto 第 {line + 1} 行: const result = value * 2;", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 48, Brushes.Black, 1);
                drawing.DrawText(text, new Point(30, 10 + line * 75));
            }
        }
        var textSource = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32);
        textSource.Render(visual);
        var textPath = Path.Combine(Root, "text-screenshot.png");
        var textEncoder = new PngBitmapEncoder();
        textEncoder.Frames.Add(BitmapFrame.Create(textSource));
        using (var output = File.Create(textPath)) textEncoder.Save(output);
        foreach (var dpi in new[] { 1.0, 1.5, 2.0, 3.0 })
        {
            var preview = ImagePreviewLoader.Load(textPath);
            var original = Render(textSource, dpi);
            var scaled = Render(preview, dpi);
            var error = original.Zip(scaled, (a, b) => Math.Abs(a - b)).Average();
            Console.WriteLine($"  text screenshot at {dpi * 100}%: mean channel error {error:F3}/255");
            // PNG encoding/decoding may round premultiplied source pixels.
            Assert(error < 0.1, "Text screenshot lost detail in the preview");
        }
        foreach (var dpi in new[] { 1.25, 1.75 })
        {
            var preview = ImagePreviewLoader.Load(textPath);
            var baseline = Render(textSource, dpi, 170, 130);
            var cached = Render(preview, dpi, 170, 130);
            var error = baseline.Zip(cached, (a, b) => Math.Abs(a - b)).Average();
            Console.WriteLine($"  fractional preview at {dpi * 100}%: mean channel error {error:F3}/255");
            Assert(error < 0.1, "Fractional DPI stretched the cached preview");
        }
        return Task.CompletedTask;
    }

    private static byte[] Render(ImageSource source, double dpi, double width = 180, double height = 140)
    {
        var image = new Image { Source = source, Width = width, Height = height, Stretch = Stretch.Uniform };
        image.Measure(new Size(width, height));
        image.Arrange(new Rect(0, 0, width, height));
        var output = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        output.Render(image);
        var bytes = new byte[output.PixelWidth * output.PixelHeight * 4];
        output.CopyPixels(bytes, output.PixelWidth * 4, 0);
        return bytes;
    }

    private static async Task PreviewLifecycle()
    {
        // Connect a real presentation source without showing or activating a
        // desktop window. This checks the same automatic sizing as the composer.
        var parameters = new System.Windows.Interop.HwndSourceParameters("IlMatto preview regression")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, no WS_VISIBLE
            PositionX = -32000, PositionY = -32000, Width = 180, Height = 140,
        };
        using var source = new System.Windows.Interop.HwndSource(parameters);
        var image = new DeferredImage { UseOriginalResolution = true, ImagePath = Path.Combine(Root, "text-screenshot.png"), Stretch = Stretch.Uniform };
        var border = new Border { Padding = new Thickness(4), BorderThickness = new Thickness(1), Child = image };
        var grid = new Grid { Width = 180, Height = 140 };
        grid.Children.Add(border);
        source.RootVisual = grid;
        grid.Measure(new Size(180, 140));
        grid.Arrange(new Rect(0, 0, 180, 140));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(image.IsLoaded && image.Source is not null && image.ActualWidth > 0, $"Automatically sized preview did not load: loaded={image.IsLoaded}, source={image.Source is not null}, actual={image.ActualWidth}x{image.ActualHeight}, slot={System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(image)}, root={grid.ActualWidth}x{grid.ActualHeight}");
        var bitmap = image.Source;
        image.DeferLoading = true;
        Assert(ReferenceEquals(image.Source, bitmap), "Deferring discarded an already visible preview");
        image.ImagePath = Path.Combine(Root, "image-64-32.png");
        Assert(image.Source is null, "A deferred path change kept the old bitmap");
        image.DeferLoading = false;
        Assert(image.Source is not null, "Preview did not resume loading");
        border.Child = null;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(image.Source is null, "Unloading retained the decoded image");
        border.Child = image;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Assert(image.IsLoaded && image.Source is not null, "Reattaching did not reload the preview");
        image.UseOriginalResolution = false;
        Assert(image.Source is BitmapSource history && history.PixelWidth == 360, "History decode width changed");
    }
}
