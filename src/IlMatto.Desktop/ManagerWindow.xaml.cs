using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private TextPointer? _emojiSelectionStartPointer;
    private TextPointer? _emojiSelectionEndPointer;
    private bool _emojiPickerInitialized;
    private bool _emojiPickerInitializing;
    private StackPanel? _recentEmojiSection;
    private ScrollViewer? _managerChatScrollViewer;
    private ScrollViewer? _airBubbleChatScrollViewer;
    private ChatTimelineController? _chatTimelineController;
    private readonly ObservableCollection<object> _chatTimelineItems = new();
    private readonly bool _useAirBubbleTimeline;
    private readonly ManagerLayoutCacheRecorder _layoutCacheRecorder;
    private bool _synchronizingInputEditor;
    private bool _inputEditorSyncQueued;
    private bool _closing;
    private bool _closeReady;

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
            // Emoji.Wpf has a second editor inside the TextBox template.  Its
            // initial binding can finish after the VM restores the draft and
            // otherwise leave the visible text and SendCommand out of sync.
            await Dispatcher.InvokeAsync(() => SynchronizeInputEditor(viewModel), DispatcherPriority.Loaded);
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

    private void ChatTimelineBubble_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_useAirBubbleTimeline || sender is not DependencyObject bubble ||
            !double.IsFinite(e.NewSize.Width) || !double.IsFinite(e.NewSize.Height))
            return;

        var presenter = FindVisualParent<ReservedMessagePresenter>(bubble);
        if (presenter?.DataContext is ChatTimelineMessageRow row)
            _chatTimelineController?.RecordNaturalBubbleSize(row, e.NewSize.Width, e.NewSize.Height);
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

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match) return match;
            parent = VisualTreeHelper.GetParent(parent);
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

    private void InputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizingInputEditor) return;

        // Emoji.Wpf owns the editor template. Keep the VM synchronized even
        // on builds where the outer TextBox only publishes its binding after
        // focus leaves the control.
        if (DataContext is ManagerViewModel viewModel)
        {
            var text = InputTextBox.Text ?? "";
            // During startup or a session switch the template may briefly
            // publish its default empty value. The VM/visible draft is the
            // source of truth while the editor is not focused; do not erase a
            // restored draft because of that transient notification.
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(viewModel.InputText) && !InputTextBox.IsKeyboardFocusWithin)
            {
                viewModel.SendCommand.NotifyCanExecuteChanged();
                return;
            }
            if (!string.Equals(viewModel.InputText, text, StringComparison.Ordinal))
                viewModel.InputText = text;
            viewModel.SendCommand.NotifyCanExecuteChanged();
        }
    }

    private void InputRichTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizingInputEditor) return;

        // The visible editor is the RichTextBox inside BorderlessEmojiTextBoxTemplate.
        if (DataContext is not ManagerViewModel viewModel || sender is not Emoji.Wpf.RichTextBox editor)
            return;

        // Emoji.Wpf raises TextChanged before its overridden handler has
        // written the normalized value back to the custom Text dependency
        // property. Reading editor.Text synchronously therefore returns the
        // previous value (the first typed character is missed). Run once after
        // the current dispatcher operation so the command sees the complete
        // text immediately, including a one-character draft.
        if (_inputEditorSyncQueued) return;
        _inputEditorSyncQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _inputEditorSyncQueued = false;
            if (_synchronizingInputEditor || DataContext is not ManagerViewModel currentViewModel)
                return;

            var text = editor.Text ?? "";
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(currentViewModel.InputText) &&
                !editor.IsKeyboardFocusWithin && !InputTextBox.IsKeyboardFocusWithin)
            {
                currentViewModel.SendCommand.NotifyCanExecuteChanged();
                return;
            }
            if (!string.Equals(currentViewModel.InputText, text, StringComparison.Ordinal))
                currentViewModel.InputText = text;
            currentViewModel.SendCommand.NotifyCanExecuteChanged();
        }));
    }

    private void SynchronizeInputEditor(ManagerViewModel viewModel)
    {
        if (!IsLoaded) return;

        _synchronizingInputEditor = true;
        try
        {
            InputTextBox.ApplyTemplate();
            var text = viewModel.InputText ?? string.Empty;
            var visibleText = InputTextBox.Text ?? string.Empty;

            // If the outer control has retained the restored draft while the
            // binding source briefly contains an empty value, preserve the
            // text the user can see. In the normal path the VM remains the
            // source of truth and is copied into both editor layers.
            if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(visibleText))
            {
                text = visibleText;
                viewModel.InputText = text;
            }
            else if (!string.Equals(visibleText, text, StringComparison.Ordinal))
            {
                InputTextBox.Text = text;
            }

            var editor = FindVisualChild<Emoji.Wpf.RichTextBox>(InputTextBox);
            if (editor is not null && !string.Equals(editor.Text, text, StringComparison.Ordinal))
                editor.Text = text;

            if (InputTextBox.CaretIndex > text.Length)
                InputTextBox.CaretIndex = text.Length;
        }
        finally
        {
            _synchronizingInputEditor = false;
        }

        // The command may have been queried before the template finished
        // binding. Re-evaluate it after both editor layers are synchronized.
        viewModel.SendCommand.NotifyCanExecuteChanged();
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

        var editor = FindVisualChild<Emoji.Wpf.RichTextBox>(InputTextBox);
        _emojiSelectionStart = InputTextBox.SelectionStart;
        _emojiSelectionLength = InputTextBox.SelectionLength;
        if (editor is not null)
        {
            // Emoji.Wpf exposes a second, emoji-aware Selection property that
            // is rebuilt from the base RichTextBox selection on selection
            // changes. Use the base selection/CaretPosition as the snapshot
            // source so a collapsed caret is not interpreted as the start of
            // the preceding text run.
            var baseEditor = (System.Windows.Controls.RichTextBox)editor;
            var selection = baseEditor.Selection;
            _emojiSelectionStartPointer = selection.IsEmpty ? baseEditor.CaretPosition : selection.Start;
            _emojiSelectionEndPointer = selection.IsEmpty ? baseEditor.CaretPosition : selection.End;
        }
        else
        {
            _emojiSelectionStartPointer = null;
            _emojiSelectionEndPointer = null;
        }
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

        EmojiPopup.IsOpen = false;
        var editor = FindVisualChild<Emoji.Wpf.RichTextBox>(InputTextBox);
        var insertedIntoEditor = false;

        // The actual caret belongs to the RichTextBox inside the Emoji.Wpf
        // template. Preserve and restore its TextPointer range so losing
        // focus to the popup cannot move insertion to index zero. Use the
        // base RichTextBox selection setter for the replacement: WPF advances
        // that selection to the end of the inserted text, while a collapsed
        // TextRange's End can remain at the old caret boundary.
        if (editor is not null && _emojiSelectionStartPointer is not null && _emojiSelectionEndPointer is not null)
        {
            try
            {
                var baseEditor = (System.Windows.Controls.RichTextBox)editor;
                var selection = baseEditor.Selection;
                editor.Focus();
                selection.Select(_emojiSelectionStartPointer, _emojiSelectionEndPointer);
                selection.Text = emoji;
                var caret = selection.End;

                var updatedText = editor.Text ?? string.Empty;
                _synchronizingInputEditor = true;
                try
                {
                    if (!string.Equals(InputTextBox.Text, updatedText, StringComparison.Ordinal))
                        InputTextBox.Text = updatedText;
                }
                finally
                {
                    _synchronizingInputEditor = false;
                }

                if (DataContext is ManagerViewModel viewModel && !string.Equals(viewModel.InputText, updatedText, StringComparison.Ordinal))
                    viewModel.InputText = updatedText;

                // Keep the caret at the end of the newly inserted emoji after
                // the outer TextBox binding has observed the new text.
                editor.Focus();
                baseEditor.Selection.Select(caret, caret);
                baseEditor.CaretPosition = caret;
                insertedIntoEditor = true;
            }
            catch (ArgumentException)
            {
                // The document may have been recreated while the popup was
                // open. Fall back to the outer control's saved index below.
            }
            catch (InvalidOperationException)
            {
                // Same fallback for a stale TextPointer from a recycled
                // template/document.
            }
        }

        if (!insertedIntoEditor)
        {
            var start = Math.Clamp(_emojiSelectionStart, 0, InputTextBox.Text?.Length ?? 0);
            var length = Math.Clamp(_emojiSelectionLength, 0, (InputTextBox.Text?.Length ?? 0) - start);
            var text = InputTextBox.Text ?? string.Empty;
            InputTextBox.Text = text.Remove(start, length).Insert(start, emoji);
            InputTextBox.CaretIndex = start + emoji.Length;
            InputTextBox.SelectionLength = 0;
        }

        _recentEmojis.Remove(emoji);
        _recentEmojis.Insert(0, emoji);
        if (_recentEmojis.Count > 24) _recentEmojis.RemoveAt(_recentEmojis.Count - 1);
        if (insertedIntoEditor)
            editor?.Focus();
        else
            InputTextBox.Focus();
        _emojiSelectionStartPointer = null;
        _emojiSelectionEndPointer = null;
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeReady || e.Cancel) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _followTimer.Stop();
        _piWorkbench?.Close();
        // Keep the dispatcher alive until the detached transcript write and
        // Host shutdown complete. Awaiting in OnClosed is too late: WPF can
        // already be shutting down when its last window has closed.
        Hide();
        try
        {
            if (DataContext is ManagerViewModel model) await model.DisposeAsync();
            _closeReady = true;
            Close();
        }
        catch (Exception exception)
        {
            _closing = false;
            Show();
            System.Windows.MessageBox.Show(this, $"会话未能保存，窗口已保留。请恢复文件访问后再次关闭。\n{exception.Message}",
                "无法完成关闭", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ManagerViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            viewModel.SettingsRequested -= OpenSettings; viewModel.OpenPiWorkbenchRequested -= OpenPiWorkbench;
            viewModel.UserMessageSent -= UserMessageSent;
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
