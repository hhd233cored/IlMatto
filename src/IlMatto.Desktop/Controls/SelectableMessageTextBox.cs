using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// A borderless, read-only text surface for ordinary chat prose. TextBox is
/// used instead of TextBlock because WPF provides selection, Ctrl+C and the
/// standard keyboard commands without a FlowDocument allocation.
/// </summary>
public sealed class SelectableMessageTextBox : System.Windows.Controls.TextBox
{
    public SelectableMessageTextBox()
    {
        IsReadOnly = true;
        IsTabStop = false;
        Focusable = true;
        AcceptsReturn = true;
        TextWrapping = TextWrapping.Wrap;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ScrollViewer.SetCanContentScroll(this, false);

        BorderThickness = new Thickness(0);
        BorderBrush = System.Windows.Media.Brushes.Transparent;
        Background = System.Windows.Media.Brushes.Transparent;
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        Cursor = System.Windows.Input.Cursors.IBeam;
        IsInactiveSelectionHighlightEnabled = true;

        SelectionBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(105, 55, 125, 190));
        SelectionTextBrush = System.Windows.Media.Brushes.Black;
        ContextMenu = CreateContextMenu();
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Command = ApplicationCommands.Copy;
        copy.CommandTarget = this;
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Command = ApplicationCommands.SelectAll;
        selectAll.CommandTarget = this;
        menu.Items.Add(selectAll);
        return menu;
    }
}

/// <summary>Provides a small copy-only menu for text surfaces without selection.</summary>
internal static class MessageCopyContextMenu
{
    public static ContextMenu Create(Func<string> textProvider)
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) =>
        {
            var text = textProvider();
            if (string.IsNullOrEmpty(text)) return;
            try { System.Windows.Clipboard.SetText(text); } catch { /* Clipboard may be temporarily busy. */ }
        };
        menu.Items.Add(copy);
        return menu;
    }
}
