using System.Windows;

namespace IlMatto.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel source)
    {
        InitializeComponent();
        var viewModel = new SettingsViewModel(source);
        viewModel.CloseRequested += result => { DialogResult = result; Close(); };
        DataContext = viewModel;
        ApiKeyBox.Password = viewModel.ApiKey;
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is System.Windows.Controls.PasswordBox passwordBox) viewModel.ApiKey = passwordBox.Password;
    }
}

