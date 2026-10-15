using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CCWrapper.Terminal;

namespace CCWrapper.Tests.Terminal;

public sealed class PasteHandlerTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ccw-paste-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static BitmapSource Bitmap() => BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);

    [Fact]
    public void QuotePaths_quotes_only_paths_with_spaces()
    {
        Assert.Equal(@"""C:\My Docs\a b.png"" C:\x\y.txt ""C:\Program Files""",
            PasteHandler.QuotePaths([@"C:\My Docs\a b.png", @"C:\x\y.txt", @"C:\Program Files"]));
        Assert.Equal("", PasteHandler.QuotePaths([]));
    }

    [Fact]
    public void Text_prefers_files_then_text()
    {
        var files = new DataObject(DataFormats.FileDrop, new[] { @"C:\a b\c.txt", @"C:\d.txt" });
        files.SetText("ignored");
        Assert.Equal(@"""C:\a b\c.txt"" C:\d.txt", PasteHandler.Text(files));

        Assert.Equal("line1\nline2", PasteHandler.Text(new DataObject(DataFormats.UnicodeText, "line1\nline2")));
    }

    [Fact]
    public void Bitmap_only_has_no_text_and_compose_saves_a_png()
    {
        var data = new DataObject(DataFormats.Bitmap, Bitmap());
        Assert.Null(PasteHandler.Text(data));
        Assert.True(PasteHandler.HasImage(data));

        var path = PasteHandler.ForCompose(data, _dir)!;
        Assert.StartsWith(_dir, path);
        Assert.EndsWith(".png", path);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(path)[..4]);
    }

    [Fact]
    public void Compose_quotes_a_saved_image_path_with_spaces_and_passes_text_through()
    {
        var spaced = Directory.CreateDirectory(Path.Combine(_dir, "with space")).FullName;
        var quoted = PasteHandler.ForCompose(new DataObject(DataFormats.Bitmap, Bitmap()), spaced)!;
        Assert.Matches(@"^"".*\.png""$", quoted);
        Assert.True(File.Exists(quoted.Trim('"')));

        Assert.Equal("hi", PasteHandler.ForCompose(new DataObject(DataFormats.UnicodeText, "hi"), spaced));
        Assert.Null(PasteHandler.ForCompose(new DataObject(), spaced));
    }
}
