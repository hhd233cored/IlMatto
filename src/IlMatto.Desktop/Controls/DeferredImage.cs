using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Defers assigning a chat attachment source while the user is rapidly
/// scrolling. Existing images stay intact; only newly realized images avoid
/// decoding until scrolling slows down.
/// </summary>
public sealed class DeferredImage : WpfImage
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(DeferredImage), new PropertyMetadata(string.Empty, OnImagePathChanged));
    public static readonly DependencyProperty DeferLoadingProperty = DependencyProperty.Register(
        nameof(DeferLoading), typeof(bool), typeof(DeferredImage), new PropertyMetadata(false, OnDeferLoadingChanged));

    public DeferredImage()
    {
        Loaded += (_, _) =>
        {
            if (!DeferLoading && Source is null) LoadImage();
        };
        Unloaded += (_, _) =>
        {
            // The timeline removes distant message rows. Releasing the
            // detached bitmap here keeps the visual subtree's memory bounded;
            // the source is loaded again when the row re-enters the viewport.
            Source = null;
        };
    }

    public string ImagePath
    {
        get => (string)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    public bool DeferLoading
    {
        get => (bool)GetValue(DeferLoadingProperty);
        set => SetValue(DeferLoadingProperty, value);
    }

    private static void OnImagePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var image = (DeferredImage)dependencyObject;
        if (!image.DeferLoading) image.LoadImage();
        else image.Source = null;
    }

    private static void OnDeferLoadingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var image = (DeferredImage)dependencyObject;
        if (!(bool)e.NewValue) image.LoadImage();
    }

    private void LoadImage()
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !System.IO.File.Exists(ImagePath))
        {
            Source = null;
            return;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new System.Uri(System.IO.Path.GetFullPath(ImagePath), System.UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 360;
            bitmap.EndInit();
            bitmap.Freeze();
            Source = bitmap;
        }
        catch
        {
            Source = null;
        }
    }
}
