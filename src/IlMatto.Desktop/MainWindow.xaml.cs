using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;

namespace IlMatto.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _chatFollowTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _followChatTail = true;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _chatFollowTimer.Tick += (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.IsBusy && _followChatTail)
                ChatScrollViewer.ScrollToEnd();
            else if (DataContext is MainViewModel { IsBusy: false })
                _chatFollowTimer.Stop();
        };
        _chatFollowTimer.Start();
        viewModel.SettingsRequested += OpenSettings;
        viewModel.QuitRequested += Close;
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            // The restored transcript can be taller than the viewport. Once
            // the Host has initialized, start the chat at the newest content.
            _followChatTail = true;
            await Dispatcher.InvokeAsync(() => ChatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsBusy)) return;
        if (sender is MainViewModel { IsBusy: true })
        {
            _chatFollowTimer.Start();
            if (_followChatTail) ChatScrollViewer.ScrollToEnd();
        }
        else
        {
            _chatFollowTimer.Stop();
        }
    }

    private void ChatScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var atBottom = e.ExtentHeight <= e.ViewportHeight || e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        // Extent changes are caused by streaming content. Only a real offset change
        // (the user moving the scrollbar) changes whether tail-following is enabled.
        if (Math.Abs(e.VerticalChange) > 0 && e.ExtentHeightChange == 0)
            _followChatTail = atBottom;
        if (DataContext is MainViewModel vm && vm.IsBusy && _followChatTail && e.ExtentHeightChange > 0)
            Dispatcher.BeginInvoke(() => ChatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ScrollViewer_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Delta == 0 || scrollViewer.ScrollableHeight <= 0) return;

        var target = Math.Clamp(
            scrollViewer.VerticalOffset - e.Delta / 3.0,
            0,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(target - scrollViewer.VerticalOffset) < 0.1) return;
        scrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void SettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void OpenSettings()
    {
        if (DataContext is MainViewModel viewModel)
            new SettingsWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosed(EventArgs e)
    {
        _chatFollowTimer.Stop();
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            viewModel.SettingsRequested -= OpenSettings;
            viewModel.QuitRequested -= Close;
        }
        if (DataContext is IAsyncDisposable disposable) await disposable.DisposeAsync();
        base.OnClosed(e);
    }
}
