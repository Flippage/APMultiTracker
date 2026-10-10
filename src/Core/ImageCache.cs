using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace APMultiTracker;

/// <summary>
/// Box art / avatar loader: small decoded bitmaps, cached on disk in %LOCALAPPDATA%\AutoSwitcher\art
/// and in memory (bounded). All calls happen on the UI thread.
/// </summary>
public static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, ImageSource> Memory = new();
    private static readonly Dictionary<string, Task<ImageSource?>> InFlight = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APMultiTracker", "art");
    private static readonly Regex SizeSuffix = new(@"-\d+x\d+(\.\w+)$", RegexOptions.Compiled);

    /// <summary>Twitch box art comes as a template ({width}x{height}) or a fixed 52x72 size; ask for 2x for HiDPI.</summary>
    public static string Normalize(string url)
    {
        url = url.Replace("{width}x{height}", "144x192");
        return SizeSuffix.Replace(url, "-144x192$1");
    }

    /// <summary>
    /// Box art as a local file with a real image extension (Windows toasts only accept local images for
    /// unpackaged apps). Reuses the disk cache; returns null if it can't be fetched.
    /// </summary>
    public static async Task<string?> GetFileAsync(string url)
    {
        try
        {
            if (url.Contains("boxart", StringComparison.OrdinalIgnoreCase) || url.Contains("{width}")) url = Normalize(url);
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
            string ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ext is not (".jpg" or ".jpeg" or ".png")) ext = ".jpg";
            string toastFile = Path.Combine(Dir, hash + ext);
            if (File.Exists(toastFile)) return toastFile;

            string cached = Path.Combine(Dir, hash + ".img");
            byte[] bytes = File.Exists(cached) ? await File.ReadAllBytesAsync(cached) : await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(toastFile, bytes);
            return toastFile;
        }
        catch { return null; }
    }

    public static Task<ImageSource?> GetAsync(string url)
    {
        if (url.Contains("boxart", StringComparison.OrdinalIgnoreCase) || url.Contains("{width}")) url = Normalize(url);
        if (Memory.TryGetValue(url, out var cached)) return Task.FromResult<ImageSource?>(cached);
        if (InFlight.TryGetValue(url, out var running)) return running;
        var task = LoadAsync(url);
        InFlight[url] = task;
        return task;
    }

    private static async Task<ImageSource?> LoadAsync(string url)
    {
        try
        {
            string file = Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Dir);
                await File.WriteAllBytesAsync(file, bytes);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 144;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();

            if (Memory.Count > 300) Memory.Clear();
            Memory[url] = bmp;
            return bmp;
        }
        catch { return null; }
        finally { InFlight.Remove(url); }
    }
}

/// <summary>Attached property: &lt;Border local:Art.Url="{Binding BoxArtUrl}"/&gt; paints the image as the border background (keeps rounded corners).</summary>
public static class Art
{
    private static readonly Brush Placeholder = CreatePlaceholder();

    private static Brush CreatePlaceholder()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Art), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject o) => (string?)o.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject o, string? value) => o.SetValue(UrlProperty, value);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        string? url = e.NewValue as string;
        border.Background = Placeholder;
        if (string.IsNullOrWhiteSpace(url)) return;

        var img = await ImageCache.GetAsync(url);
        if (img != null && GetUrl(border) == url)
        {
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            border.Background = brush;
        }
    }
}
