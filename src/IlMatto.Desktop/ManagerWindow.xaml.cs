using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IlMatto.Desktop.Models;

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
            await viewModel.DisposeAsync();
        }
        base.OnClosed(e);
    }
}
