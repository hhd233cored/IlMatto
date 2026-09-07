using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMessageBox = System.Windows.MessageBox;

namespace IlMatto.Desktop;

public partial class AvatarCropWindow : Window
{
    private readonly string _sourcePath;
    private readonly string _outputPath;
    private BitmapSource? _sourceBitmap;
    private double _displayScale;
    private double _imageLeft;
    private double _imageTop;
    private double _displayWidth;
    private double _displayHeight;
    private double _cropLeft;
    private double _cropTop;
    private double _cropDiameter;
    private bool _dragging;
    private System.Windows.Point _dragStart;
    private double _dragStartLeft;
    private double _dragStartTop;

    public AvatarCropWindow(string sourcePath, string outputPath)
    {
        InitializeComponent();
        _sourcePath = Path.GetFullPath(sourcePath);
        _outputPath = Path.GetFullPath(outputPath);
        Loaded += (_, _) => LoadPreview();
    }

    public string? SavedPath { get; private set; }

    public static string DefaultOutputPath(string slot) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IlMatto", "avatars", $"{slot}-avatar.png");

    private void LoadPreview()
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(_sourcePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            _sourceBitmap = bitmap;

            var canvasSize = Math.Min(CropCanvas.Width, CropCanvas.Height);
            _displayScale = Math.Min(canvasSize / bitmap.PixelWidth, canvasSize / bitmap.PixelHeight);
            _displayWidth = bitmap.PixelWidth * _displayScale;
            _displayHeight = bitmap.PixelHeight * _displayScale;
            _imageLeft = (CropCanvas.Width - _displayWidth) / 2;
            _imageTop = (CropCanvas.Height - _displayHeight) / 2;
            PreviewImage.Source = bitmap;
            PreviewImage.Width = _displayWidth;
            PreviewImage.Height = _displayHeight;
            Canvas.SetLeft(PreviewImage, _imageLeft);
            Canvas.SetTop(PreviewImage, _imageTop);

            _cropDiameter = Math.Min(_displayWidth, _displayHeight) * CropSizeSlider.Value;
            _cropLeft = _imageLeft + (_displayWidth - _cropDiameter) / 2;
            _cropTop = _imageTop + (_displayHeight - _cropDiameter) / 2;
            UpdateCropVisual();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, $"无法打开图片：{exception.Message}", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
        }
    }

    private void CropSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sourceBitmap is null || _cropDiameter <= 0) return;
        var centerX = _cropLeft + _cropDiameter / 2;
        var centerY = _cropTop + _cropDiameter / 2;
        _cropDiameter = Math.Min(_displayWidth, _displayHeight) * CropSizeSlider.Value;
        _cropLeft = centerX - _cropDiameter / 2;
        _cropTop = centerY - _cropDiameter / 2;
        ClampCropToImage();
        UpdateCropVisual();
    }

    private void CropCanvas_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(CropCanvas);
        var centerX = _cropLeft + _cropDiameter / 2;
        var centerY = _cropTop + _cropDiameter / 2;
        if (Math.Pow(point.X - centerX, 2) + Math.Pow(point.Y - centerY, 2) > Math.Pow(_cropDiameter / 2, 2)) return;
        _dragging = true;
        _dragStart = point;
        _dragStartLeft = _cropLeft;
        _dragStartTop = _cropTop;
        CropCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void CropCanvas_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CropCanvas);
        _cropLeft = _dragStartLeft + point.X - _dragStart.X;
        _cropTop = _dragStartTop + point.Y - _dragStart.Y;
        ClampCropToImage();
        UpdateCropVisual();
    }

    private void CropCanvas_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        CropCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ClampCropToImage()
    {
        _cropLeft = Math.Clamp(_cropLeft, _imageLeft, _imageLeft + _displayWidth - _cropDiameter);
        _cropTop = Math.Clamp(_cropTop, _imageTop, _imageTop + _displayHeight - _cropDiameter);
    }

    private void UpdateCropVisual()
    {
        CropCircle.Width = _cropDiameter;
        CropCircle.Height = _cropDiameter;
        Canvas.SetLeft(CropCircle, _cropLeft);
        Canvas.SetTop(CropCircle, _cropTop);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sourceBitmap is null || _displayScale <= 0) return;
        try
        {
            var x = Math.Clamp((int)Math.Round((_cropLeft - _imageLeft) / _displayScale), 0, _sourceBitmap.PixelWidth - 1);
            var y = Math.Clamp((int)Math.Round((_cropTop - _imageTop) / _displayScale), 0, _sourceBitmap.PixelHeight - 1);
            var size = Math.Max(1, (int)Math.Round(_cropDiameter / _displayScale));
            size = Math.Min(size, Math.Min(_sourceBitmap.PixelWidth - x, _sourceBitmap.PixelHeight - y));

            var cropped = new CroppedBitmap(_sourceBitmap, new Int32Rect(x, y, size, size));
            cropped.Freeze();
            var rendered = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.PushClip(new EllipseGeometry(new Rect(0, 0, 256, 256)));
                context.DrawImage(cropped, new Rect(0, 0, 256, 256));
                context.Pop();
            }
            rendered.Render(drawing);
            rendered.Freeze();

            var directory = Path.GetDirectoryName(_outputPath)!;
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using (var stream = File.Create(_outputPath)) encoder.Save(stream);
            SavedPath = _outputPath;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, $"保存头像失败：{exception.Message}", "IlMatto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
