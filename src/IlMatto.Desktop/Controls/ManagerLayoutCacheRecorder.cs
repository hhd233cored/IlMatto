using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IlMatto.Desktop.Infrastructure;
using IlMatto.Desktop.Models;
using WpfListBox = System.Windows.Controls.ListBox;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// First-stage recorder for the fully realized chat list. It does not change
/// what is rendered; after layout settles it records the actual row and bubble
/// bounds so a later placeholder mode can reuse them.
/// </summary>
internal sealed class ManagerLayoutCacheRecorder : IDisposable
{
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(160);

    private readonly WpfListBox _list;
    private readonly DispatcherTimer _captureTimer;
    private ManagerConversationItem? _conversation;
    private bool _disposed;

    public ManagerLayoutCacheRecorder(WpfListBox list)
    {
        _list = list;
        _captureTimer = new DispatcherTimer(DispatcherPriority.Background, list.Dispatcher)
        {
            Interval = CaptureDelay,
        };
        _captureTimer.Tick += CaptureTimerOnTick;
    }

    public void SetConversation(ManagerConversationItem? conversation)
    {
        _conversation = conversation;
        Schedule();
    }

    public void Schedule()
    {
        if (_disposed || _conversation is null) return;
        _captureTimer.Stop();
        _captureTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureTimer.Stop();
        _captureTimer.Tick -= CaptureTimerOnTick;
    }

    private void CaptureTimerOnTick(object? sender, EventArgs e)
    {
        _captureTimer.Stop();
        if (_disposed || _conversation is null || _list.ActualWidth <= 0) return;

        var conversation = _conversation;
        var stableMessages = conversation.Messages
            .Where(IsStableMessage)
            .ToList();
        if (stableMessages.Count == 0) return;

        var captured = new List<ManagerMessageLayoutCacheEntry>(stableMessages.Count);
        var contentWidth = 0d;
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var entry = conversation.Messages[index];
            if (!IsStableMessage(entry)) continue;
            if (_list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return;
            if (item.ActualHeight < 24 || !double.IsFinite(item.ActualHeight)) return;

            var messageRow = FindNamedDescendant<FrameworkElement>(item, "MessageRow");
            var messageBubble = FindNamedDescendant<FrameworkElement>(item, "MessageBubble");
            contentWidth = messageRow?.ActualWidth > 0 ? messageRow.ActualWidth : contentWidth;
            captured.Add(new ManagerMessageLayoutCacheEntry
            {
                MessageIndex = index,
                RowHeight = item.ActualHeight,
                BubbleHeight = messageBubble?.ActualHeight is > 0 and var bubbleHeight ? bubbleHeight : 0,
                BubbleWidth = messageBubble?.ActualWidth is > 0 and var bubbleWidth ? bubbleWidth : 0,
            });
        }

        // The first phase is a full-list baseline. Do not write a partial file
        // if a layout pass has not created every stable item yet.
        if (captured.Count != stableMessages.Count) return;
        if (contentWidth <= 0)
            contentWidth = Math.Max(1, _list.ActualWidth - _list.Padding.Left - _list.Padding.Right);

        try
        {
            ManagerLayoutCacheStore.Save(
                conversation.SessionId,
                ManagerLayoutCacheStore.GetWidthBucket(contentWidth),
                captured);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[layout-cache] 保存失败：{exception.Message}");
        }
    }

    private static bool IsStableMessage(ManagerChatEntry entry) =>
        !entry.IsTransientStatus && !entry.IsStreamingText && !entry.IsThinking;

    private static T? FindNamedDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T named && string.Equals(named.Name, name, StringComparison.Ordinal)) return named;
            var descendant = FindNamedDescendant<T>(child, name);
            if (descendant is not null) return descendant;
        }
        return null;
    }
}
