using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IlMatto.Desktop;

public partial class ImageViewerWindow : Window
{
    // Keep the viewer's maximum window size aligned with ManagerWindow's
    // work-area rule: 16 DIP of margin on each side.
    private const double WorkAreaMargin = 16;
    private const double ViewerPadding = 36;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private readonly ScaleTransform _imageScale = new(1, 1);
    private double _zoom = 1;
    private bool _imageLoaded;

    public ImageViewerWindow(string imagePath, string? displayName = null)
    {
        InitializeComponent();
        ImagePreview.LayoutTransform = _imageScale;
        Loaded += (_, _) =>
        {
            FitImageToViewer();
            AlignToOwnerTop();
        };

        var safeName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(imagePath)
            : displayName;
        Title = string.IsNullOrWhiteSpace(safeName) ? "图片查看器" : safeName;
        ImageNameText.Text = string.IsNullOrWhiteSpace(safeName) ? "图片" : safeName;

        try
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!File.Exists(fullPath))
            {
                ImageNameText.Text = $"图片不存在：{safeName}";
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();
            ImagePreview.Source = bitmap;
            _imageLoaded = true;
        }
        catch (Exception exception)
        {
            ImageNameText.Text = $"无法打开图片：{exception.Message}";
        }
    }

    private void FitImageToViewer()
    {
        if (!_imageLoaded || ImagePreview.Source is not BitmapSource bitmap) return;

        var workAreaWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
        var workAreaHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
        var maxWindowWidth = Math.Max(MinWidth, workAreaWidth - WorkAreaMargin * 2);
        var maxWindowHeight = Math.Max(MinHeight, workAreaHeight - WorkAreaMargin * 2);
        var availableWidth = Math.Max(1, maxWindowWidth - ViewerPadding);
        var availableHeight = Math.Max(1, maxWindowHeight - ViewerPadding);
        var widthScale = availableWidth / bitmap.PixelWidth;
        var heightScale = availableHeight / bitmap.PixelHeight;
        _zoom = Math.Min(1, Math.Min(widthScale, heightScale));

        var displayedWidth = Math.Max(1, Math.Ceiling(bitmap.PixelWidth * _zoom));
        var displayedHeight = Math.Max(1, Math.Ceiling(bitmap.PixelHeight * _zoom));
        MaxWidth = maxWindowWidth;
        MaxHeight = maxWindowHeight;
        Width = Math.Clamp(displayedWidth + ViewerPadding, MinWidth, maxWindowWidth);
        Height = Math.Clamp(displayedHeight + ViewerPadding, MinHeight, maxWindowHeight);
        ApplyZoom();
    }

    private void ImageScrollViewer_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_imageLoaded) return;

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        ApplyZoom();
        e.Handled = true;
    }

    private void ApplyZoom()
    {
        _imageScale.ScaleX = _zoom;
        _imageScale.ScaleY = _zoom;
        ZoomText.Text = $"{_zoom:P0} · 滚轮缩放";
    }

    private void AlignToOwnerTop()
    {
        if (Owner is not Window owner || !owner.IsLoaded) return;

        var workArea = SystemParameters.WorkArea;
        var centeredLeft = owner.Left + (owner.ActualWidth - Width) / 2;
        Left = Math.Clamp(
            centeredLeft,
            workArea.Left + WorkAreaMargin,
            workArea.Right - Width - WorkAreaMargin);
        Top = Math.Clamp(
            owner.Top,
            workArea.Top + WorkAreaMargin,
            workArea.Bottom - Height - WorkAreaMargin);
    }

    private void ViewerHeader_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        DragMove();
        e.Handled = true;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
