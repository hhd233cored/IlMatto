using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.DataObject;
using WpfDataFormats = System.Windows.DataFormats;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>
/// Owns the short-lived clipboard lease used by the hidden interactive AGY
/// session. The broker deliberately exposes no general clipboard API to the
/// ManagerHost: only a managed image can be placed in the clipboard.
/// </summary>
public sealed class ClipboardBroker
{
    private static readonly object Gate = new();
    private static ClipboardLease? ActiveLease;

    public ClipboardLease BeginImagePaste(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("图片路径不能为空。", nameof(imagePath));
        var fullPath = Path.GetFullPath(imagePath);
        var image = LoadBitmap(fullPath);
        var original = CaptureClipboard();
        var fingerprint = Fingerprint(image);

        lock (Gate)
        {
            if (ActiveLease is not null && !ActiveLease.IsReleased)
                throw new InvalidOperationException("系统剪贴板正在被另一条图片请求占用。");

            try
            {
                var data = new WpfDataObject();
                data.SetData(WpfDataFormats.Bitmap, image, false);
                WpfClipboard.SetDataObject(data, true);
            }
            catch
            {
                // The caller cannot safely restore here because the target
                // image may not have reached the clipboard at all. The
                // original snapshot remains owned by the failed lease only.
                throw;
            }

            var lease = new ClipboardLease(original, fingerprint);
            ActiveLease = lease;
            return lease;
        }
    }

    private static WpfDataObject CaptureClipboard()
    {
        var snapshot = new WpfDataObject();
        var source = WpfClipboard.GetDataObject();
        if (source is null) return snapshot;

        foreach (var format in new[]
                 {
                     WpfDataFormats.UnicodeText, WpfDataFormats.Text, WpfDataFormats.Html,
                     WpfDataFormats.Rtf, WpfDataFormats.FileDrop
                 })
        {
            try
            {
                if (!source.GetDataPresent(format, true)) continue;
                var value = source.GetData(format, true);
                switch (value)
                {
                    case string text: snapshot.SetData(format, text, false); break;
                    case string[] files: snapshot.SetData(format, files, false); break;
                    case System.Collections.Specialized.StringCollection collection:
                        snapshot.SetData(format, collection, false); break;
                }
            }
            catch { /* A locked or vendor-specific format is non-fatal. */ }
        }

        try
        {
            if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is { } bitmap)
                snapshot.SetData(WpfDataFormats.Bitmap, bitmap.Clone(), false);
        }
        catch { }
        return snapshot;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            throw new InvalidOperationException("图片文件不存在或不是普通文件。");

        var image = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }

    private static string Fingerprint(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static bool TryGetCurrentImageFingerprint(out string? fingerprint)
    {
        try
        {
            fingerprint = WpfClipboard.GetImage() is { } image ? Fingerprint(image) : null;
            return true;
        }
        catch
        {
            fingerprint = null;
            return false;
        }
    }

    public sealed class ClipboardLease : IDisposable
    {
        private readonly WpfDataObject _original;
        private readonly string _temporaryFingerprint;
        private int _released;

        internal ClipboardLease(WpfDataObject original, string temporaryFingerprint)
        {
            _original = original;
            _temporaryFingerprint = temporaryFingerprint;
        }

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>
        /// Restores the old clipboard only while the clipboard still contains
        /// the image installed by this lease. A user copy operation therefore
        /// always wins over restoration.
        /// </summary>
        public bool Restore()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return false;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (!TryGetCurrentImageFingerprint(out var currentFingerprint))
                    {
                        if (attempt < 2) { Thread.Sleep(35 * (attempt + 1)); continue; }
                        ClearActive(this);
                        return false;
                    }
                    if (!string.Equals(currentFingerprint, _temporaryFingerprint, StringComparison.Ordinal))
                    {
                        ClearActive(this);
                        return false;
                    }

                    try
                    {
                        WpfClipboard.SetDataObject(_original, true);
                        ClearActive(this);
                        return true;
                    }
                    catch when (attempt < 2)
                    {
                        Thread.Sleep(35 * (attempt + 1));
                    }
                }
            }
            catch { }
            ClearActive(this);
            return false;
        }

        public void Dispose() => Restore();

        private static void ClearActive(ClipboardLease lease)
        {
            lock (Gate)
            {
                if (ReferenceEquals(ActiveLease, lease)) ActiveLease = null;
            }
        }
    }
}
