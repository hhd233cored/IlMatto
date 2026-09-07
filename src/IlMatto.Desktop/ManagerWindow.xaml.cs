using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EmojiDataModel = Emoji.Wpf.EmojiData;
using EmojiTextBlock = Emoji.Wpf.TextBlock;
using IlMatto.Desktop.Models;
using IlMatto.Desktop.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfPoint = System.Windows.Point;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace IlMatto.Desktop;

public partial class ManagerWindow : Window
{
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _followTail = true;
    private MainWindow? _piWorkbench;
    private readonly List<string> _recentEmojis = new();
    private int _emojiSelectionStart;
    private int _emojiSelectionLength;
    private bool _emojiPickerInitialized;
    private bool _emojiPickerInitializing;
    private StackPanel? _recentEmojiSection;
    private ScrollViewer? _managerChatScrollViewer;
    private readonly DispatcherTimer _fastScrollSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Stopwatch _scrollStopwatch = Stopwatch.StartNew();
    private long _lastScrollTimestamp;
    private readonly ChatRenderRecoveryScheduler _chatRenderRecoveryScheduler;
    private Thumb? _chatScrollThumb;
    private ChatScrollMode _chatScrollMode;

    private enum ChatScrollMode
    {
        Idle,
        WheelFast,
        ThumbDrag,
        Settling,
    }

    public ManagerWindow()
    {
        InitializeComponent();
        _chatRenderRecoveryScheduler = new ChatRenderRecoveryScheduler(Dispatcher);
        var viewModel = new ManagerViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        viewModel.SettingsRequested += OpenSettings;
        viewModel.OpenPiWorkbenchRequested += OpenPiWorkbench;
        viewModel.UserMessageSent += UserMessageSent;
        SourceInitialized += (_, _) => FitWindowToWorkArea();
        _followTimer.Tick += (_, _) => { if (DataContext is ManagerViewModel { IsBusy: true } && _followTail) GetChatScrollViewer()?.ScrollToEnd(); else _followTimer.Stop(); };
        _fastScrollSettleTimer.Tick += (_, _) => BeginChatRenderRecovery();
        Closed += (_, _) => { _fastScrollSettleTimer.Stop(); _chatRenderRecoveryScheduler.Dispose(); ResetChatScrollPipeline(); };
        Loaded += async (_, _) => { await viewModel.InitializeAsync(); await Dispatcher.InvokeAsync(() => GetChatScrollViewer()?.ScrollToEnd(), DispatcherPriority.Background); };
    }

    private void OpenPiWorkbench()
    {
        if (_piWorkbench is { IsLoaded: true }) { _piWorkbench.Activate(); return; }
        _piWorkbench = new MainWindow { Owner = this };
        _piWorkbench.Closed += (_, _) => _piWorkbench = null;
        _piWorkbench.Show();
    }

    private void OpenSettings()
    {
        if (DataContext is ManagerViewModel viewModel) new ManagerSettingsWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void RenameConversation_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ManagerViewModel viewModel || viewModel.SelectedConversation is not { } conversation) return;

        var input = new System.Windows.Controls.TextBox
        {
            Text = conversation.Title,
            MinWidth = 320,
            Margin = new Thickness(0, 8, 0, 14),
            MaxLength = 120,
        };
        var dialog = new Window
        {
            Owner = this,
            Title = "重命名会话",
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        var saveButton = new System.Windows.Controls.Button { Content = "保存", IsDefault = true, MinWidth = 72, Padding = new Thickness(12, 5, 12, 5) };
        var cancelButton = new System.Windows.Controls.Button { Content = "取消", IsCancel = true, MinWidth = 72, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        saveButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                System.Windows.MessageBox.Show(dialog, "会话标题不能为空。", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Warning);
                input.Focus();
                return;
            }
            viewModel.RenameSelectedConversation(input.Text);
            dialog.DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "会话标题", Foreground = System.Windows.Media.Brushes.DimGray });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManagerViewModel.IsBusy))
        {
            if (sender is ManagerViewModel { IsBusy: true }) { _followTimer.Start(); if (_followTail) GetChatScrollViewer()?.ScrollToEnd(); }
            else _followTimer.Stop();
        }
        if (e.PropertyName == nameof(ManagerViewModel.SelectedConversation)) ResetChatScrollPipeline();
        if (e.PropertyName is nameof(ManagerViewModel.ChatEntries) or nameof(ManagerViewModel.SelectedConversation))
            Dispatcher.BeginInvoke(() => GetChatScrollViewer()?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UserMessageSent()
    {
        _followTail = true;
        Dispatcher.BeginInvoke(() => GetChatScrollViewer()?.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ManagerChatScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        var atBottom = e.ExtentHeight <= e.ViewportHeight || e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        if (Math.Abs(e.VerticalChange) > 0 && e.ExtentHeightChange == 0)
        {
            _followTail = atBottom;
            if (_chatScrollMode != ChatScrollMode.ThumbDrag && !(_followTail && DataContext is ManagerViewModel { IsBusy: true }))
                ObserveChatScrollVelocity(e.VerticalChange);
        }
        if (DataContext is ManagerViewModel { IsBusy: true } && _followTail && _chatScrollMode == ChatScrollMode.Idle && e.ExtentHeightChange > 0)
            Dispatcher.BeginInvoke(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ObserveChatScrollVelocity(double verticalChange)
    {
        var now = _scrollStopwatch.ElapsedMilliseconds;
        var elapsed = Math.Max(1, now - _lastScrollTimestamp);
        _lastScrollTimestamp = now;
        var velocity = Math.Abs(verticalChange) / elapsed;
        if (Math.Abs(verticalChange) >= 160 || velocity >= 1.2)
        {
            EnterFastChatScrolling(ChatScrollMode.WheelFast);
            RestartFastScrollSettleTimer();
        }
        else if (_chatScrollMode == ChatScrollMode.WheelFast)
        {
            // Keep the height reservation alive for a brief quiet period. A
            // low-speed event immediately after a fling is not proof that all
            // FlowDocuments have settled.
            RestartFastScrollSettleTimer();
        }
    }

    private void EnterFastChatScrolling(ChatScrollMode mode)
    {
        _chatRenderRecoveryScheduler.Cancel();
        FreezeRealizedMarkdown();
        _chatScrollMode = mode;
        if (DataContext is ManagerViewModel viewModel)
        {
            viewModel.IsChatMarkdownRenderingDeferred = true;
            viewModel.IsFastChatScrolling = true;
        }
    }

    private void ManagerChatList_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_managerChatScrollViewer is not null) return;
        _managerChatScrollViewer = FindVisualChild<ScrollViewer>(ManagerChatList);
        if (_managerChatScrollViewer is null) return;
        _managerChatScrollViewer.ScrollChanged += ManagerChatScrollViewer_OnScrollChanged;
        AttachThumbDragEvents(_managerChatScrollViewer);
    }

    private ScrollViewer? GetChatScrollViewer() => _managerChatScrollViewer ??= FindVisualChild<ScrollViewer>(ManagerChatList);

    private void AttachThumbDragEvents(ScrollViewer scrollViewer)
    {
        if (_chatScrollThumb is not null) return;
        var verticalBar = FindVisualChildren<WpfScrollBar>(scrollViewer).FirstOrDefault(bar => bar.Orientation == WpfOrientation.Vertical);
        _chatScrollThumb = verticalBar is null ? null : FindVisualChildren<Thumb>(verticalBar).FirstOrDefault();
        if (_chatScrollThumb is null) return;
        _chatScrollThumb.DragStarted += ChatScrollThumb_OnDragStarted;
        _chatScrollThumb.DragCompleted += ChatScrollThumb_OnDragCompleted;
    }

    private void ChatScrollThumb_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        _followTail = false;
        _fastScrollSettleTimer.Stop();
        EnterFastChatScrolling(ChatScrollMode.ThumbDrag);
    }

    private void ChatScrollThumb_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_chatScrollMode != ChatScrollMode.ThumbDrag) return;
        _chatScrollMode = ChatScrollMode.Settling;
        RestartFastScrollSettleTimer();
    }

    private void RestartFastScrollSettleTimer()
    {
        _fastScrollSettleTimer.Stop();
        _fastScrollSettleTimer.Start();
    }

    private void BeginChatRenderRecovery()
    {
        _fastScrollSettleTimer.Stop();
        if (_chatScrollMode is ChatScrollMode.Idle or ChatScrollMode.ThumbDrag) return;
        _chatScrollMode = ChatScrollMode.Settling;
        FreezeRealizedMarkdown();

        if (DataContext is ManagerViewModel viewModel)
            viewModel.IsChatMarkdownRenderingDeferred = false;

        var scrollViewer = GetChatScrollViewer();
        var candidates = FindVisualChildren<MarkdownViewer>(ManagerChatList)
            .Where(viewer => viewer.IsRenderPending)
            .Select(viewer => new { Viewer = viewer, Distance = GetViewerDistanceFromViewport(viewer, scrollViewer), IsVisible = IsViewerInViewport(viewer, scrollViewer) })
            .OrderBy(item => item.Distance)
            .ToArray();

        var visible = candidates.Where(item => item.IsVisible).Select(item => item.Viewer).ToArray();
        var prefetch = candidates.Where(item => !item.IsVisible && item.Distance <= Math.Max(300, scrollViewer?.ViewportHeight * 0.5 ?? 300)).Select(item => item.Viewer).ToArray();
        _chatRenderRecoveryScheduler.Start(visible, prefetch, CompleteVisibleChatRecovery);
    }

    private void CompleteVisibleChatRecovery()
    {
        if (_chatScrollMode != ChatScrollMode.Settling) return;
        // Viewers that already had a correct document never entered the
        // recovery queue. Re-enable their permit now so a later operation
        // expansion or streamed-text completion can render normally.
        foreach (var viewer in FindVisualChildren<MarkdownViewer>(ManagerChatList).Where(viewer => !viewer.IsRenderPending))
            viewer.SetRenderPermit(true);
        _chatScrollMode = ChatScrollMode.Idle;
        if (DataContext is ManagerViewModel viewModel) viewModel.IsFastChatScrolling = false;
    }

    private void ResetChatScrollPipeline()
    {
        _fastScrollSettleTimer.Stop();
        _chatRenderRecoveryScheduler.Cancel();
        _chatScrollMode = ChatScrollMode.Idle;
        if (DataContext is ManagerViewModel viewModel)
        {
            viewModel.IsChatMarkdownRenderingDeferred = false;
            viewModel.IsFastChatScrolling = false;
        }
    }

    private void FreezeRealizedMarkdown()
    {
        foreach (var viewer in FindVisualChildren<MarkdownViewer>(ManagerChatList)) viewer.SetRenderPermit(false);
    }

    private static bool IsViewerInViewport(FrameworkElement viewer, ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null || !viewer.IsLoaded) return false;
        try
        {
            var bounds = viewer.TransformToAncestor(scrollViewer).TransformBounds(new Rect(new WpfPoint(), viewer.RenderSize));
            return bounds.Bottom >= 0 && bounds.Top <= scrollViewer.ViewportHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static double GetViewerDistanceFromViewport(FrameworkElement viewer, ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null || !viewer.IsLoaded) return double.MaxValue;
        try
        {
            var bounds = viewer.TransformToAncestor(scrollViewer).TransformBounds(new Rect(new WpfPoint(), viewer.RenderSize));
            if (bounds.Bottom < 0) return -bounds.Bottom;
            if (bounds.Top > scrollViewer.ViewportHeight) return bounds.Top - scrollViewer.ViewportHeight;
            return 0;
        }
        catch (InvalidOperationException) { return double.MaxValue; }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void Image_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not FrameworkElement { DataContext: ManagerImageAttachment attachment }) return;

        var viewer = new ImageViewerWindow(attachment.Path, attachment.DisplayName) { Owner = this };
        viewer.Show();
        e.Handled = true;
    }

    private void InputTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DataContext is ManagerViewModel imageViewModel)
        {
            // Let ordinary text paste follow WPF's normal behavior. When the
            // clipboard contains an image, capture it as a managed attachment
            // instead of inserting an opaque bitmap placeholder into the text.
            if (imageViewModel.TryAddClipboardImage()) e.Handled = true;
            if (e.Handled) return;
        }
        if (e.Key != Key.Enter) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (sender is System.Windows.Controls.TextBox textBox)
            {
                var selectionStart = textBox.SelectionStart;
                textBox.SelectedText = Environment.NewLine;
                textBox.CaretIndex = selectionStart + Environment.NewLine.Length;
                textBox.SelectionLength = 0;
            }
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (DataContext is ManagerViewModel viewModel && viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
    }

    private void InputTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (DataContext is not ManagerViewModel viewModel) return;
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Bitmap, true) && !System.Windows.Clipboard.ContainsImage()) return;
        if (viewModel.TryAddClipboardImage()) e.CancelCommand();
    }

    private async void EmojiButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (EmojiPopup.IsOpen)
        {
            EmojiPopup.IsOpen = false;
            return;
        }

        _emojiSelectionStart = InputTextBox.SelectionStart;
        _emojiSelectionLength = InputTextBox.SelectionLength;
        EmojiPopup.IsOpen = true;
        await InitializeEmojiPickerAsync();
    }

    private async Task InitializeEmojiPickerAsync()
    {
        if (_emojiPickerInitialized || _emojiPickerInitializing) return;
        _emojiPickerInitializing = true;
        try
        {
            EmojiGroupPanel.Children.Clear();
            EmojiCategoryPanel.Children.Clear();
            _recentEmojiSection = null;
            EmojiGroupPanel.IsEnabled = false;
            EmojiCategoryPanel.IsEnabled = false;

            EmojiGroupPanel.Children.Add(new TextBlock
            {
                Text = "正在加载表情…",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 13,
                Margin = new Thickness(8, 10, 8, 10),
            });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
            EmojiGroupPanel.Children.Clear();

            var emojiCount = 0;
            var groups = EmojiDataModel.AllGroups
                .Where(group => string.Equals(group.Name, "Smileys & Emotion", StringComparison.OrdinalIgnoreCase))
                .Take(1)
                .ToList();
            if (groups.Count == 0)
            {
                var fallbackGroup = EmojiDataModel.AllGroups.FirstOrDefault();
                if (fallbackGroup is not null) groups.Add(fallbackGroup);
            }

            foreach (var group in groups)
            {
                var section = new StackPanel { Margin = new Thickness(2, 0, 2, 10) };
                section.Children.Add(new TextBlock
                {
                    Text = group.Name,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    FontSize = 12,
                    Margin = new Thickness(4, 2, 4, 5),
                });
                var emojiPanel = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
                section.Children.Add(emojiPanel);
                EmojiGroupPanel.Children.Add(section);

                if (EmojiCategoryPanel.Children.Count == 0)
                {
                    var categoryButton = new WpfButton
                    {
                        Style = (Style)FindResource("EmojiCategoryButton"),
                        ToolTip = group.Name,
                        Content = CreateEmojiVisual(string.IsNullOrWhiteSpace(group.Icon) ? "•" : group.Icon, 19),
                    };
                    categoryButton.Click += (_, _) => section.BringIntoView();
                    EmojiCategoryPanel.Children.Add(categoryButton);
                }

                foreach (var emoji in group.EmojiList.Select(item => item.Text).Where(value => !string.IsNullOrEmpty(value)))
                {
                    emojiPanel.Children.Add(CreateEmojiButton(emoji));
                    emojiCount++;
                    if (emojiCount % 32 == 0)
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
                }
            }

            _emojiPickerInitialized = true;
            UpdateRecentEmojiSection();
        }
        catch (Exception exception)
        {
            EmojiGroupPanel.Children.Clear();
            EmojiGroupPanel.Children.Add(new TextBlock
            {
                Text = $"表情加载失败：{exception.Message}",
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 10, 8, 10),
            });
        }
        finally
        {
            EmojiGroupPanel.IsEnabled = true;
            EmojiCategoryPanel.IsEnabled = true;
            _emojiPickerInitializing = false;
        }
    }

    private void EmojiPopup_OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EmojiGroupPanel.UpdateLayout();
            EmojiCategoryPanel.UpdateLayout();
            EmojiScrollViewer.UpdateLayout();
            EmojiScrollViewer.ScrollToHome();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateRecentEmojiSection()
    {
        if (!_emojiPickerInitialized) return;
        if (_recentEmojiSection is not null)
        {
            EmojiGroupPanel.Children.Remove(_recentEmojiSection);
            _recentEmojiSection = null;
        }
        if (_recentEmojis.Count == 0) return;

        _recentEmojiSection = (StackPanel)CreateEmojiSection("最近使用", _recentEmojis);
        EmojiGroupPanel.Children.Insert(0, _recentEmojiSection);
    }

    private FrameworkElement CreateEmojiSection(string title, IEnumerable<string> emojis)
    {
        var section = new StackPanel { Margin = new Thickness(2, 0, 2, 10) };
        section.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(4, 2, 4, 5),
        });

        var emojiPanel = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        foreach (var emoji in emojis.Where(value => !string.IsNullOrEmpty(value)))
            emojiPanel.Children.Add(CreateEmojiButton(emoji));
        section.Children.Add(emojiPanel);
        return section;
    }

    private WpfButton CreateEmojiButton(string emoji)
    {
        var button = new WpfButton
        {
            Style = (Style)FindResource("EmojiTileButton"),
            ToolTip = emoji,
            Content = CreateEmojiVisual(emoji, 24),
        };
        button.Click += (_, _) => InsertEmoji(emoji);
        return button;
    }

    private static EmojiTextBlock CreateEmojiVisual(string emoji, double fontSize)
    {
        return new EmojiTextBlock
        {
            Text = emoji,
            FontSize = fontSize,
            ColorBlend = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
    }

    private void InsertEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;

        var start = Math.Clamp(_emojiSelectionStart, 0, InputTextBox.Text?.Length ?? 0);
        var length = Math.Clamp(_emojiSelectionLength, 0, (InputTextBox.Text?.Length ?? 0) - start);
        var text = InputTextBox.Text ?? string.Empty;
        InputTextBox.Text = text.Remove(start, length).Insert(start, emoji);
        InputTextBox.CaretIndex = start + emoji.Length;
        InputTextBox.SelectionLength = 0;
        _recentEmojis.Remove(emoji);
        _recentEmojis.Insert(0, emoji);
        if (_recentEmojis.Count > 24) _recentEmojis.RemoveAt(_recentEmojis.Count - 1);
        EmojiPopup.IsOpen = false;
        InputTextBox.Focus();
        Dispatcher.BeginInvoke(UpdateRecentEmojiSection, DispatcherPriority.Background);
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ManagerViewModel viewModel && viewModel.IsProcessDrawerOpen)
        {
            viewModel.CloseDrawersCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _followTimer.Stop();
        _piWorkbench?.Close();
        if (DataContext is ManagerViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            viewModel.SettingsRequested -= OpenSettings; viewModel.OpenPiWorkbenchRequested -= OpenPiWorkbench;
        viewModel.UserMessageSent -= UserMessageSent;
        await viewModel.DisposeAsync();
        }
        base.OnClosed(e);
    }

    private void FitWindowToWorkArea()
    {
        const double margin = 16;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(MinWidth, workArea.Width - margin * 2);
        var availableHeight = Math.Max(MinHeight, workArea.Height - margin * 2);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = workArea.Left + Math.Max(margin, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(margin, (workArea.Height - Height) / 2);
    }
}
