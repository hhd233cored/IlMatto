using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IlMatto.Desktop;

public partial class ManagerWindow : Window
{
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _followTail = true;
    private MainWindow? _piWorkbench;

    public ManagerWindow()
    {
        InitializeComponent();
        var viewModel = new ManagerViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        viewModel.SettingsRequested += OpenSettings;
        viewModel.OpenPiWorkbenchRequested += OpenPiWorkbench;
        _followTimer.Tick += (_, _) => { if (DataContext is ManagerViewModel { IsBusy: true } && _followTail) ManagerChatScrollViewer.ScrollToEnd(); else _followTimer.Stop(); };
        Loaded += async (_, _) => { await viewModel.InitializeAsync(); await Dispatcher.InvokeAsync(() => ManagerChatScrollViewer.ScrollToEnd(), DispatcherPriority.Background); };
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

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ManagerViewModel.IsBusy)) return;
        if (sender is ManagerViewModel { IsBusy: true }) { _followTimer.Start(); if (_followTail) ManagerChatScrollViewer.ScrollToEnd(); }
        else _followTimer.Stop();
    }

    private void ManagerChatScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var atBottom = e.ExtentHeight <= e.ViewportHeight || e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        if (Math.Abs(e.VerticalChange) > 0 && e.ExtentHeightChange == 0) _followTail = atBottom;
        if (DataContext is ManagerViewModel { IsBusy: true } && _followTail && e.ExtentHeightChange > 0)
            Dispatcher.BeginInvoke(() => ManagerChatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.ScrollableHeight <= 0) return;
        scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset - e.Delta / 3.0, 0, scroll.ScrollableHeight)); e.Handled = true;
    }

    private void InputTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DataContext is ManagerViewModel viewModel && viewModel.SendCommand.CanExecute(null))
        { viewModel.SendCommand.Execute(null); e.Handled = true; }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosed(EventArgs e)
    {
        _followTimer.Stop();
        _piWorkbench?.Close();
        if (DataContext is ManagerViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            viewModel.SettingsRequested -= OpenSettings; viewModel.OpenPiWorkbenchRequested -= OpenPiWorkbench;
            await viewModel.DisposeAsync();
        }
        base.OnClosed(e);
    }
}
