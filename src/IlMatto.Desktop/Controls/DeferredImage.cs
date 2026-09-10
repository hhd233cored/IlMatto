using System.Windows;
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
    public static readonly DependencyProperty UseOriginalResolutionProperty = DependencyProperty.Register(
        nameof(UseOriginalResolution), typeof(bool), typeof(DeferredImage), new PropertyMetadata(false, OnImagePathChanged));

    public DeferredImage()
    {
        Loaded += (_, _) =>
        {
            LoadImage();
        };
        Unloaded += (_, _) =>
        {
            // The timeline removes distant message rows. Releasing the
            // detached bitmap here keeps the visual subtree's memory bounded;
            // the source is loaded again when the row re-enters the viewport.
            Source = null;
            _loadedPath = null;
        };
    }

    private string? _loadedPath;
    public bool UseOriginalResolution
    {
        get => (bool)GetValue(UseOriginalResolutionProperty);
        set => SetValue(UseOriginalResolutionProperty, value);
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
        image.Source = null;
        image._loadedPath = null;
        image.LoadImage();
    }

    private static void OnDeferLoadingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var image = (DeferredImage)dependencyObject;
        if (!(bool)e.NewValue) image.LoadImage();
    }

    private void LoadImage()
    {
        // An automatically sized Image has no dimensions until it has a
        // source. Loading must not depend on its current render size.
        if (!IsLoaded || DeferLoading) return;
        if (string.IsNullOrWhiteSpace(ImagePath) || !System.IO.File.Exists(ImagePath))
        {
            Source = null;
            return;
        }
        try
        {
            if (Source is not null && _loadedPath == ImagePath) return;
            if (UseOriginalResolution)
                Source = ImagePreviewLoader.Load(ImagePath);
            else
            {
                // Keep the established history-image rendering unchanged.
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new System.Uri(System.IO.Path.GetFullPath(ImagePath), System.UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 360;
                bitmap.EndInit();
                bitmap.Freeze();
                Source = bitmap;
            }
            _loadedPath = ImagePath;
        }
        catch
        {
            Source = null;
        }
    }
}
