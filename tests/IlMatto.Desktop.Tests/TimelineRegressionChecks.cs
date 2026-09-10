using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using IlMatto.Desktop.Controls;
using IlMatto.Desktop.Models;

internal static class TimelineRegressionChecks
{
    internal static async Task Geometry(string fixtureRoot)
    {
        var imagePath = Path.Combine(fixtureRoot, "timeline-image.png");
        var image = BitmapSource.Create(1200, 900, 96, 96, PixelFormats.Bgr32, null, new byte[1200 * 900 * 4], 1200 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(imagePath)) encoder.Save(output);
        using var fixture = new TimelineFixture();
        var entries = new ObservableCollection<ManagerChatEntry>(Enumerable.Range(0, 36).Select(i =>
            new ManagerChatEntry(i % 2 == 0 ? "Agent" : "你", "antigravity",
                string.Join("\n\n", Enumerable.Repeat($"第 {i} 条消息。测试聊天换行和 **粗体**，内容需在滚动前后保持相同高度。", i % 4 + 1)),
                attachments: i % 5 == 0 ? new[] { ManagerImageAttachment.FromPath(imagePath), ManagerImageAttachment.FromPath(imagePath) } : null)
            { ShowDateSeparator = i % 7 == 0 }));
        fixture.Controller.SetEntries(entries);
        await fixture.Settle();
        fixture.AssertGeometry("initial");
        foreach (var offset in new[] { 600d, 1400, 2800, 1000, 0 })
        {
            fixture.Scroll.ScrollToVerticalOffset(offset);
            await fixture.Settle();
            fixture.AssertGeometry($"offset {offset}");
        }
        // Warm every row, then revisit them repeatedly. A measured row must
        // replace its spacer without changing the total scrollbar range.
        for (var offset = 0d; offset < fixture.Scroll.ScrollableHeight; offset += 400)
        {
            fixture.Scroll.ScrollToVerticalOffset(offset);
            await fixture.Settle();
        }
        fixture.Scroll.ScrollToEnd();
        await fixture.Settle();
        var extent = fixture.Scroll.ExtentHeight;
        foreach (var offset in new[] { 0d, 1800, 600, 2800, 1000, 0 })
        {
            fixture.Scroll.ScrollToVerticalOffset(offset);
            await fixture.Settle();
            fixture.AssertGeometry($"revisit {offset}");
            Require(Math.Abs(fixture.Scroll.ExtentHeight - extent) < 0.5, "Rematerializing measured rows changed the scroll extent");
        }
        fixture.Scroll.ScrollToVerticalOffset(1800);
        await fixture.Settle();
        var anchor = fixture.FirstVisibleRow();
        var before = fixture.RowY(anchor);
        var above = fixture.Items.OfType<ChatTimelineMessageRow>().First(row => row.Index < anchor.Index);
        above.Entry.IsStreamingText = true;
        above.Entry.Append(string.Concat(Enumerable.Repeat("\n新增内容，应当保持正在阅读的消息位置。", 5)));
        await fixture.Settle();
        fixture.AssertGeometry("stream above viewport");
        Require(Math.Abs(fixture.RowY(anchor) - before) < 0.5, $"Reading anchor moved by {fixture.RowY(anchor) - before:F2} pixels after an earlier row grew");
        above.Entry.IsStreamingText = false;
        await fixture.Settle();
        fixture.AssertGeometry("completed above viewport");
        Require(Math.Abs(fixture.RowY(anchor) - before) < 0.5, "Completion moved the reading anchor");
        above.Entry.ShowDateSeparator = !above.Entry.ShowDateSeparator;
        await fixture.Settle();
        fixture.AssertGeometry("date header above viewport");
        Require(Math.Abs(fixture.RowY(anchor) - before) < 0.5, "Date header change moved the reading anchor");

        // Exercise routed events from the actual scrollbar thumb, not just
        // direct controller calls. New visible rows must render through the
        // normal queue while the mouse is still held, before DragCompleted.
        fixture.StartThumbDrag();
        var dragExtent = fixture.Scroll.ExtentHeight;
        foreach (var offset in new[] { fixture.Scroll.ScrollableHeight, 0, 1400, fixture.Scroll.ScrollableHeight })
        {
            fixture.Scroll.ScrollToVerticalOffset(offset);
            await fixture.Settle();
            var rows = fixture.Items.OfType<ChatTimelineMessageRow>().ToArray();
            Require(rows.Length > 0 && rows.All(row => row.RenderContent), "Thumb dragging paused the normal render queue until release");
            fixture.AssertRenderedText();
            fixture.AssertGeometry($"thumb held at {offset:F0}");
            Require(Math.Abs(fixture.Scroll.ExtentHeight - dragExtent) < 0.5, "Dragging changed the measured scroll extent");
        }
        fixture.CompleteThumbDrag();
        await fixture.Settle();
        fixture.AssertGeometry("thumb released");
        Require(fixture.Items.OfType<ChatTimelineMessageRow>().Any(row => row.RenderContent), "Thumb release discarded rendered content");
        foreach (var width in new[] { 620d, 940, 1100 })
        {
            fixture.ResizeWidth(width);
            await fixture.Settle();
            fixture.AssertGeometry($"width {width}");
            fixture.Scroll.ScrollToVerticalOffset(600);
            await fixture.Settle();
            fixture.AssertGeometry($"resized revisit {width}");
        }
        await PresenterLifecycle();
    }

    private static async Task PresenterLifecycle()
    {
        var entry = new ManagerChatEntry("Agent", "antigravity", "initial measure");
        var presenter = new ReservedMessagePresenter
        {
            Message = entry,
            FullContentTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Border Width=\"180\" Height=\"140\"/></DataTemplate>"),
        };
        var count = 0;
        presenter.NaturalHeightMeasured += (_, _) => count++;
        var root = new Grid();
        root.Children.Add(presenter);
        using var source = new HwndSource(new HwndSourceParameters("IlMatto initial measurement regression")
        { WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000, Width = 300, Height = 200 });
        source.RootVisual = root;
        root.Measure(new Size(300, 200));
        root.Arrange(new Rect(0, 0, 300, 200));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Require(count > 0, "The initial presentation-source measurement was discarded");
        presenter.InvalidateMeasure();
        root.UpdateLayout();
        var beforeDetach = count;
        root.Children.Remove(presenter);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Require(count == beforeDetach, "A detached presenter published a stale measurement");
    }

    internal static Task IndexConsistency()
    {
        var cache = new ChatMessageLayoutCache();
        var layout = new ChatTimelineLayoutIndex(cache);
        var entries = Enumerable.Range(0, 40).Select(i => new ManagerChatEntry("Agent", "antigravity", $"消息 {i}")).ToList();
        layout.Synchronize(entries, 874);
        var random = new Random(17);
        for (var pass = 0; pass < 200; pass++)
        {
            var index = random.Next(entries.Count);
            var height = random.Next(30, 20000) + random.NextDouble();
            entries[index].IsStreamingText = true;
            layout.UpdateMeasuredHeight(index, 874, height);
            Require(Math.Abs(layout.GetHeight(index) - height) < 0.5, "Live measured height was replaced by an estimate or truncated");
            var total = Enumerable.Range(0, entries.Count).Sum(layout.GetHeight);
            Require(Math.Abs(layout.TotalHeight - total) < 0.001, "Fenwick total differs from the sum of row heights");
            var top = 0d;
            for (var row = 0; row < entries.Count; row++)
            {
                Require(Math.Abs(layout.GetTop(row) - top) < 0.001, "Row top differs from the prefix sum");
                Require(layout.FindIndexAtOffset(top + layout.GetHeight(row) / 2) == row, "Offset lookup returned the wrong row");
                top += layout.GetHeight(row);
            }
            layout.Invalidate(entries[index], 874);
            Require(Math.Abs(layout.GetHeight(index) - height) < 0.5, "Invalidation discarded the last actual height");
        }
        var prior = layout.TotalHeight;
        var added = new ManagerChatEntry("你", "user", "新消息");
        entries.Add(added);
        layout.Synchronize(entries, 874);
        Require(Math.Abs(layout.TotalHeight - layout.GetHeight(entries.Count - 1) - prior) < 0.001, "Appending reset existing streaming measurements");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TimelineFixture : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Grid _root;
        private readonly ListBox _list;
        private readonly HashSet<ReservedMessagePresenter> _subscribed = new();
        internal readonly ObservableCollection<object> Items = new();
        internal ChatTimelineController Controller { get; }
        internal ScrollViewer Scroll { get; }

        internal TimelineFixture()
        {
            // Use the production templates/styles without running window
            // commands, Host initialization, settings, or persistent caches.
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var window = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagerWindow.xaml")).Root!;
            var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml")).Root!;
            var resourceElements = app.Element(wpf + "Application.Resources")!.Elements()
                .Concat(window.Element(wpf + "Window.Resources")!.Elements()).Select(element => new XElement(element));
            var root = new XElement(wpf + "Grid", window.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                new XAttribute(x + "Name", "RootWindow"), new XAttribute("Width", 940), new XAttribute("Height", 580),
                new XElement(wpf + "Grid.Resources", resourceElements),
                new XElement(wpf + "ListBox", new XAttribute(x + "Name", "Timeline"),
                    new XAttribute("Style", "{StaticResource ChatHistoryList}"),
                    new XAttribute("ItemContainerStyle", "{StaticResource ChatHistoryItem}"),
                    new XAttribute("ItemTemplateSelector", "{StaticResource ChatTimelineItemSelector}")));
            root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:IlMatto.Desktop.Controls;assembly=IlMatto.Desktop");
            foreach (var element in root.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:IlMatto.Desktop.Controls"))
                element.Name = XName.Get(element.Name.LocalName, "clr-namespace:IlMatto.Desktop.Controls;assembly=IlMatto.Desktop");
            root.Descendants(wpf + "EventSetter").Remove();
            foreach (var attribute in root.Descendants().Attributes().Where(attribute =>
                         !attribute.IsNamespaceDeclaration && attribute.Value.Contains("_On", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
            _root = (Grid)XamlReader.Parse(root.ToString());
            _list = (ListBox)_root.FindName("Timeline");
            _list.ItemsSource = Items;
            _source = new HwndSource(new HwndSourceParameters("IlMatto timeline regression")
            { WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000, Width = 940, Height = 580 });
            _source.RootVisual = _root;
            _root.Measure(new Size(940, 580));
            _root.Arrange(new Rect(0, 0, 940, 580));
            _root.UpdateLayout();
            Scroll = Descendants<ScrollViewer>(_list).First();
            Controller = new ChatTimelineController(_list, Scroll, Items);
            Controller.Attach();
        }

        internal async Task Settle()
        {
            for (var pass = 0; pass < 12; pass++)
            {
                _root.UpdateLayout();
                foreach (var presenter in Descendants<ReservedMessagePresenter>(_list))
                    if (_subscribed.Add(presenter))
                    {
                        presenter.NaturalHeightMeasured += (_, measurement) =>
                        {
                            if (presenter.DataContext is ChatTimelineMessageRow row)
                                Controller.RecordNaturalContentHeight(row, measurement);
                        };
                        presenter.InvalidateMeasure();
                    }
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
        }

        internal void AssertGeometry(string phase)
        {
            Console.WriteLine($"  {phase}: list={_list.ActualWidth}, viewport={Scroll.ViewportWidth}, padding={_list.Padding}, extent={Scroll.ExtentHeight}, offset={Scroll.VerticalOffset}");
            var errors = new List<string>();
            foreach (var row in Items.OfType<ChatTimelineMessageRow>().Where(row => row.RenderContent))
            {
                var container = (ListBoxItem)_list.ItemContainerGenerator.ContainerFromItem(row);
                var error = Math.Abs(container.ActualHeight - row.ReservedHeight);
                if (error > 0.5) errors.Add($"#{row.Index} measured={container.ActualHeight:F2} indexed={row.ReservedHeight:F2}");
            }
            if (errors.Count > 0) throw new InvalidOperationException($"Timeline {phase}: {string.Join("; ", errors)}");
            var indexedExtent = Items.Sum(item => item is ChatTimelineMessageRow row ? row.ReservedHeight : ((ChatTimelineSpacer)item).Height);
            Require(Math.Abs(indexedExtent - Scroll.ExtentHeight) < 0.5, $"Index total {indexedExtent:F2} differs from scrollbar extent {Scroll.ExtentHeight:F2}");
        }

        internal double RowY(ChatTimelineMessageRow row) =>
            ((ListBoxItem)_list.ItemContainerGenerator.ContainerFromItem(row)).TransformToAncestor(Scroll).Transform(new Point()).Y;

        internal void AssertRenderedText() => Require(Descendants<AdaptiveMarkdownPresenter>(_list)
            .Any(presenter => !string.IsNullOrWhiteSpace(presenter.Markdown) && presenter.Content is not null),
            "Visible Markdown content was not created while dragging");

        internal ChatTimelineMessageRow FirstVisibleRow() => Items.OfType<ChatTimelineMessageRow>()
            .First(row => RowY(row) + ((ListBoxItem)_list.ItemContainerGenerator.ContainerFromItem(row)).ActualHeight > 0);

        private Thumb ScrollThumb => Descendants<ScrollBar>(Scroll)
            .Where(bar => bar.Orientation == Orientation.Vertical && ReferenceEquals(bar.TemplatedParent, Scroll))
            .SelectMany(Descendants<Thumb>).First();

        internal void StartThumbDrag() => ScrollThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        internal void CompleteThumbDrag() => ScrollThumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        internal void ResizeWidth(double width) => _root.Width = width;

        public void Dispose()
        {
            Controller.Dispose();
            _source.Dispose();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
