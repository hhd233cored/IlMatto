using System.IO;
using System.Windows.Media.Imaging;

namespace IlMatto.Desktop.Controls;

internal static class ImagePreviewLoader
{
    internal static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        // Keep the same pixels and WPF filtering as the original composer.
        // The control owns this decode only while loaded; attachment models
        // retain the path, so sent images cannot pin full previews in history.
        return bitmap;
    }
}
