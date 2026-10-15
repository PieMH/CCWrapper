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
    public void Text_drops_ESC_so_a_pasted_end_marker_cannot_end_the_bracketed_paste()
    {
        Assert.Equal("a[201~rm -rf /\rb", PasteHandler.Text(new DataObject(DataFormats.UnicodeText, "a\x1b[201~rm -rf /\rb")));
        Assert.Null(PasteHandler.Text(new DataObject(DataFormats.UnicodeText, "\x1b\x1b")));
    }

    [Fact]
    public void Control_characters_other_than_tab_cr_lf_are_dropped()
    {
        // C0 (Ctrl+C, Ctrl+D, BEL, BS), DEL and C1 (CSI U+009B, NEL U+0085) go; tab, CR, LF and other text stay
        Assert.Equal("a\tb\r\nc dé€", PasteHandler.StripControls("a\tb\r\n\u0003c\u0004 \u0007\bd\u007f\u009b\u0085é€"));
        Assert.Equal("x y", PasteHandler.Text(new DataObject(DataFormats.UnicodeText, "x\u0003 \u009by")));
    }

    [Fact]
    public void Bracketed_paste_wraps_clean_text_with_cr_newlines()
    {
        Assert.Equal("\x1b[200~one\rtwo\rthree[201~x\x1b[201~", PasteHandler.Bracketed("one\r\ntwo\nthree\x1b[201~\u0003x"));
        Assert.Equal("\x1b[200~\x1b[201~", PasteHandler.Bracketed("\u001b\u009b"));
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
