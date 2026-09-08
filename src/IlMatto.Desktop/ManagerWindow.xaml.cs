using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EmojiDataModel = Emoji.Wpf.EmojiData;
using EmojiTextBlock = Emoji.Wpf.TextBlock;
using IlMatto.Desktop.Controls;
using IlMatto.Desktop.Models;
using WpfButton = System.Windows.Controls.Button;

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
    private ScrollViewer? _airBubbleChatScrollViewer;
    private ChatTimelineController? _chatTimelineController;
    private readonly ObservableCollection<object> _chatTimelineItems = new();
    private readonly bool _useAirBubbleTimeline;
    private readonly ManagerLayoutCacheRecorder _layoutCacheRecorder;

    public ManagerWindow()
    {
        InitializeComponent();
        var viewModel = new ManagerViewModel();
        // The placeholder timeline is the default phase-two path. Setting
        // ILMATTO_CHAT_RENDER_MODE=full or =air temporarily overrides the
        // persisted setting for profiling and visual regression diagnostics.
        var renderModeOverride = Environment.GetEnvironmentVariable("ILMATTO_CHAT_RENDER_MODE");
        _useAirBubbleTimeline = renderModeOverride?.Equals("full", StringComparison.OrdinalIgnoreCase) == true
            ? false
            : renderModeOverride?.Equals("air", StringComparison.OrdinalIgnoreCase) == true
                ? true
                : !viewModel.UseFullChatRendering;
        ManagerChatList.Visibility = _useAirBubbleTimeline ? Visibility.Collapsed : Visibility.Visible;
        AirBubbleChatList.Visibility = _useAirBubbleTimeline ? Visibility.Visible : Visibility.Collapsed;
        AirBubbleChatList.ItemsSource = _chatTimelineItems;
        _layoutCacheRecorder = new ManagerLayoutCacheRecorder(ManagerChatList);
        DataContext = viewModel;
        if (!_useAirBubbleTimeline) _layoutCacheRecorder.SetConversation(viewModel.SelectedConversation);
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        viewModel.SettingsRequested += OpenSettings;
        viewModel.OpenPiWorkbenchRequested += OpenPiWorkbench;
        viewModel.UserMessageSent += UserMessageSent;
        SourceInitialized += (_, _) => FitWindowToWorkArea();
        _followTimer.Tick += (_, _) => { if (DataContext is ManagerViewModel { IsBusy: true } && _followTail) GetChatScrollViewer()?.ScrollToEnd(); else _followTimer.Stop(); };
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            if (_useAirBubbleTimeline)
                _chatTimelineController?.SetEntries(viewModel.ChatEntries, viewModel.SelectedConversation?.SessionId);
            await Dispatcher.InvokeAsync(() => GetChatScrollViewer()?.ScrollToEnd(), DispatcherPriority.Background);
            // FlowDocument and Emoji.Wpf initialization is expensive only once
            // per process. Schedule it after the window is responsive so the
            // first historical conversation does not pay that setup cost.
            _ = Dispatcher.BeginInvoke(MarkdownViewer.WarmUp, DispatcherPriority.ApplicationIdle);
        };
        Closed += (_, _) =>
        {
            _layoutCacheRecorder.Dispose();
            _chatTimelineController?.Dispose();
            DisposeManagerChatScrollViewer();
        };
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
        if (e.PropertyName is nameof(ManagerViewModel.ChatEntries) or nameof(ManagerViewModel.SelectedConversation))
        {
            if (sender is ManagerViewModel viewModel)
            {
                if (_useAirBubbleTimeline)
                    _chatTimelineController?.SetEntries(viewModel.ChatEntries, viewModel.SelectedConversation?.SessionId);
                else
                    _layoutCacheRecorder.SetConversation(viewModel.SelectedConversation);
            }
            else if (!_useAirBubbleTimeline)
            {
                _layoutCacheRecorder.Schedule();
            }
            Dispatcher.BeginInvoke(() => GetChatScrollViewer()?.ScrollToEnd(), DispatcherPriority.Background);
        }
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
        if (Math.Abs(e.VerticalChange) > 0 && e.ExtentHeightChange == 0) _followTail = atBottom;
        if (DataContext is ManagerViewModel { IsBusy: true } && _followTail && e.ExtentHeightChange > 0)
            Dispatcher.BeginInvoke(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ManagerChatList_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_useAirBubbleTimeline) return;
        _layoutCacheRecorder.Schedule();
        if (_managerChatScrollViewer is not null) return;
        _managerChatScrollViewer = FindVisualChild<ScrollViewer>(ManagerChatList);
        if (_managerChatScrollViewer is null) return;
        _managerChatScrollViewer.ScrollChanged += ManagerChatScrollViewer_OnScrollChanged;
    }

    private void AirBubbleChatList_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_useAirBubbleTimeline || _airBubbleChatScrollViewer is not null) return;
        _airBubbleChatScrollViewer = FindVisualChild<ScrollViewer>(AirBubbleChatList);
        if (_airBubbleChatScrollViewer is null) return;

        _airBubbleChatScrollViewer.ScrollChanged += ManagerChatScrollViewer_OnScrollChanged;
        _chatTimelineController = new ChatTimelineController(AirBubbleChatList, _airBubbleChatScrollViewer, _chatTimelineItems);
        // Phase-three timeline: rows begin as fixed-height shells and the
        // controller promotes only viewport-near rows to the full Markdown /
        // image template. The full ListBox remains available for profiling.
        _chatTimelineController.PlaceholderOnly = false;
        _chatTimelineController.Attach();
        if (DataContext is ManagerViewModel viewModel)
            _chatTimelineController.SetEntries(viewModel.ChatEntries, viewModel.SelectedConversation?.SessionId);
    }

    private void ChatHistoryItem_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_useAirBubbleTimeline) _layoutCacheRecorder.Schedule();
    }

    private void ChatHistoryItem_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_useAirBubbleTimeline) _layoutCacheRecorder.Schedule();
    }

    private void ChatTimelinePresenter_OnNaturalHeightMeasured(object sender, ChatMessageMeasuredEventArgs e)
    {
        if (sender is ReservedMessagePresenter presenter &&
            presenter.DataContext is ChatTimelineMessageRow row)
            _chatTimelineController?.RecordNaturalContentHeight(row, e);
    }

    private void DisposeManagerChatScrollViewer()
    {
        if (_managerChatScrollViewer is not null)
            _managerChatScrollViewer.ScrollChanged -= ManagerChatScrollViewer_OnScrollChanged;
        _managerChatScrollViewer = null;
        if (_airBubbleChatScrollViewer is not null)
            _airBubbleChatScrollViewer.ScrollChanged -= ManagerChatScrollViewer_OnScrollChanged;
        _airBubbleChatScrollViewer = null;
    }

    private ScrollViewer? GetChatScrollViewer()
    {
        if (_useAirBubbleTimeline)
            return _airBubbleChatScrollViewer ??= FindVisualChild<ScrollViewer>(AirBubbleChatList);
        return _managerChatScrollViewer ??= FindVisualChild<ScrollViewer>(ManagerChatList);
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
