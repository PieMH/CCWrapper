using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CCWrapper.Terminal;

/// <summary>Clipboard or drop data → what to insert. Pure: callers read the clipboard and catch its COM errors.</summary>
public static class PasteHandler
{
    /// <summary>Alt+V: Claude Code's own Windows image paste, which reads the bitmap from the clipboard itself.</summary>
    public const string ImagePasteKeys = "\x1bv";

    /// <summary>Files (Explorer copy) as quoted paths, else the text; null when there is neither (e.g. only a bitmap).</summary>
    public static string? Text(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) return QuotePaths(files);
        return data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text ? text : null;
    }

    public static bool HasImage(IDataObject data) => data.GetDataPresent(DataFormats.Bitmap);

    /// <summary>For the compose box: <see cref="Text"/>, else a bitmap saved as a PNG and returned as its quoted path.</summary>
    public static string? ForCompose(IDataObject data, string? dir = null) =>
        Text(data) ?? (data.GetData(DataFormats.Bitmap) is BitmapSource image ? QuotePaths([SaveImage(image, dir)]) : null);

    /// <summary>Saves to %LOCALAPPDATA%\CCWrapper\paste (or <paramref name="dir"/>) and returns the file path.</summary>
    public static string SaveImage(BitmapSource image, string? dir = null)
    {
        dir ??= Path.Combine(ClaudeLaunch.AppDataDir, "paste");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    /// <summary>Space-separated; a path with spaces is double-quoted (Windows paths cannot contain quotes).</summary>
    public static string QuotePaths(IEnumerable<string> paths) =>
        string.Join(' ', paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
}
