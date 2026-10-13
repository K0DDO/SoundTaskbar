using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace SoundTaskbar.Helpers;

internal static class AppIconLoader
{
    private const int OutputSize = 16;
    private static readonly ConcurrentDictionary<string, BitmapImage?> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> ExeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<BitmapImage?> TryLoadAsync(string? appId) =>
        Task.Run(() => TryLoad(appId));

    public static BitmapImage? TryLoad(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        if (ImageCache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        BitmapImage? image = null;
        try
        {
            var exe = ResolveExecutable(appId);
            if (exe is null || !File.Exists(exe))
            {
                ImageCache[appId] = null;
                return null;
            }

            using var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon is null)
            {
                ImageCache[appId] = null;
                return null;
            }

            using var colored = icon.ToBitmap();
            using var mono = ToMonochromeWhite(colored, OutputSize);
            using var ms = new MemoryStream();
            mono.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.DecodePixelWidth = OutputSize;
            image.EndInit();
            image.Freeze();
        }
        catch
        {
            image = null;
        }

        ImageCache[appId] = image;
        return image;
    }

    /// <summary>White silhouette: alpha from luminance, RGB = white.</summary>
    private static Bitmap ToMonochromeWhite(Bitmap source, int size)
    {
        using var scaled = new Bitmap(source, size, size);
        var result = new Bitmap(size, size, DrawingPixelFormat.Format32bppArgb);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var p = scaled.GetPixel(x, y);
                if (p.A < 8)
                {
                    result.SetPixel(x, y, DrawingColor.Transparent);
                    continue;
                }

                // Prefer alpha from source; boost with luminance so dark glyphs stay visible.
                var lum = (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                var alpha = Math.Clamp((p.A * Math.Max(lum, 40)) / 255, 0, 255);
                result.SetPixel(x, y, DrawingColor.FromArgb(alpha, 242, 244, 247));
            }
        }

        return result;
    }

    private static string? ResolveExecutable(string appId)
    {
        if (ExeCache.TryGetValue(appId, out var cachedExe))
        {
            return cachedExe;
        }

        var id = appId.Trim();
        var lower = id.ToLowerInvariant();
        string? path = null;

        if (lower.EndsWith(".exe", StringComparison.Ordinal) && File.Exists(id))
        {
            path = id;
        }
        else
        {
            var needle = GuessProcessNeedle(lower);
            if (needle.Length > 0)
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        var name = process.ProcessName;
                        if (name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            needle.Contains(name, StringComparison.OrdinalIgnoreCase))
                        {
                            var file = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                            {
                                path = file;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Access denied for some system processes
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        ExeCache[appId] = path;
        return path;
    }

    private static string GuessProcessNeedle(string appIdLower)
    {
        if (appIdLower.Contains("spotify", StringComparison.Ordinal))
        {
            return "spotify";
        }

        if (appIdLower.Contains("chrome", StringComparison.Ordinal))
        {
            return "chrome";
        }

        if (appIdLower.Contains("msedge", StringComparison.Ordinal) ||
            appIdLower.Contains("microsoftedge", StringComparison.Ordinal))
        {
            return "msedge";
        }

        if (appIdLower.Contains("firefox", StringComparison.Ordinal))
        {
            return "firefox";
        }

        if (appIdLower.Contains("yandex", StringComparison.Ordinal))
        {
            return "yandex";
        }

        if (appIdLower.Contains("vlc", StringComparison.Ordinal))
        {
            return "vlc";
        }

        var bang = appIdLower.IndexOf('!');
        var head = bang > 0 ? appIdLower[..bang] : appIdLower;
        var under = head.IndexOf('_');
        if (under > 0)
        {
            head = head[..under];
        }

        var dot = head.LastIndexOf('.');
        if (dot >= 0 && dot < head.Length - 1)
        {
            head = head[(dot + 1)..];
        }

        return head.Length >= 3 ? head : string.Empty;
    }
}
