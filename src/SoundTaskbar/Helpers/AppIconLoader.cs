using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SoundTaskbar.Helpers;

internal static class AppIconLoader
{
    public static Task<BitmapImage?> TryLoadAsync(string? appId) =>
        Task.Run(() => TryLoad(appId));

    public static BitmapImage? TryLoad(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        try
        {
            var exe = ResolveExecutable(appId);
            if (exe is null || !File.Exists(exe))
            {
                return null;
            }

            using var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutable(string appId)
    {
        var id = appId.Trim();
        var lower = id.ToLowerInvariant();

        // Direct path / exe name
        if (lower.EndsWith(".exe", StringComparison.Ordinal) && File.Exists(id))
        {
            return id;
        }

        var needle = GuessProcessNeedle(lower);
        if (needle.Length == 0)
        {
            return null;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    needle.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
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

        return null;
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

        // AppUserModelId: Family!App → take token before _ or .
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
