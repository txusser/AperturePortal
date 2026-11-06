using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ApertureOS;

/// <summary>
/// Loads cover art for the library grids at roughly tile size instead of full resolution.
///
/// Binding Image.Source straight to the file path makes WPF decode every cover at its native size
/// (a 600x900 SteamGridDB grid is ~2 MB of bitmap) on the UI thread, which with a full Steam +
/// family library made every filter/tab change take seconds. Decoding to tile height is several
/// times cheaper, and a small cache makes flipping back to a tab nearly free.
/// </summary>
public sealed class CoverImageConverter : IValueConverter
{
    public static readonly CoverImageConverter Instance = new();

    // Tiles are 240 px (desktop) / 288 px (Console Mode) tall; decode a bit larger so covers stay
    // sharp on a high-DPI TV.
    private const int DecodePixelHeight = 480;
    private const int MaxCachedImages = 200;

    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        if (_cache.TryGetValue(path, out var cached))
            return cached;

        if (!File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            // Read the file now and release it: covers get deleted/replaced when games are
            // removed or re-arted, which a lazily-held file handle would block.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = DecodePixelHeight;
            image.EndInit();
            image.Freeze();

            _cache[path] = image;
            _cacheOrder.Enqueue(path);
            while (_cacheOrder.Count > MaxCachedImages)
                _cache.Remove(_cacheOrder.Dequeue());

            return image;
        }
        catch (Exception)
        {
            // A corrupt or half-written image file just shows as no cover.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
