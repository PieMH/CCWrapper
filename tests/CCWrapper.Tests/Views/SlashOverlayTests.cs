using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Views;

public sealed class SlashOverlayTests
{
    static readonly Brush Blue = Frozen(Colors.Blue), Grey = Frozen(Colors.Gray); // frozen: each test lays out on its own thread

    static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>Lays a TextBox and its overlay out, not shown, and reads back what the overlay drew: no pixels, its drawing.</summary>
    static List<(Brush Brush, Rect Bounds)> Drawn(string text, Action<TextBox>? setUp = null)
    {
        List<(Brush, Rect)>? drawn = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var input = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Padding = new Thickness(10, 8, 10, 8), FontSize = 14 };
                var overlay = new SlashOverlay { Input = input, Commands = [new("model", "", "t", "[model]"), new("compact", "", "t")] };
                var grid = new Grid { Children = { input, overlay } };
                grid.Resources["SlashText"] = Blue;
                grid.Resources["Muted"] = Grey;
                input.CaretIndex = text.Length;
                setUp?.Invoke(input);
                grid.Measure(new Size(300, 200));
                grid.Arrange(new Rect(0, 0, 300, 200));
                grid.UpdateLayout();
                overlay.InvalidateVisual();
                grid.UpdateLayout();
                drawn = [];
                Collect(VisualTreeHelper.GetDrawing(overlay), Transform.Identity, drawn);
                // each blue glyph sits where the TextBox laid out its character
                foreach (var (start, end, _) in SlashWord.Commands(text, overlay.Commands))
                    for (var i = start; i < end; i++)
                    {
                        var at = input.GetRectFromCharacterIndex(i);
                        Assert.Contains(drawn, d => d.Item1 == Blue && Math.Abs(d.Item2.Left - at.Left) < 3 && d.Item2.Top >= at.Top - 1 && d.Item2.Bottom <= at.Bottom + 1);
                    }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
        return drawn!;
    }

    static void Collect(Drawing? drawing, Transform outer, List<(Brush, Rect)> into)
    {
        if (drawing is DrawingGroup group)
        {
            var transform = group.Transform is { } t ? new TransformGroup { Children = { t, outer } } : outer;
            foreach (var child in group.Children) Collect(child, transform, into);
        }
        else if (drawing is GlyphRunDrawing glyphs) into.Add((glyphs.ForegroundBrush, outer.TransformBounds(glyphs.Bounds)));
    }

    [Fact]
    public void Known_commands_are_drawn_blue_glyph_by_glyph_and_the_hint_grey()
    {
        var drawn = Drawn("please /compact then /model ");
        Assert.Equal("/compact/model".Length, drawn.Count(d => d.Brush == Blue));
        Assert.Single(drawn, d => d.Brush == Grey);
    }

    [Fact]
    public void Unknown_words_paths_and_urls_draw_nothing()
    {
        Assert.Empty(Drawn("/foo a/model https://x/model"));
    }

    [Fact]
    public void A_wrapped_command_still_lands_on_its_glyphs()
    {
        var drawn = Drawn(new string('x', 120) + " /model");
        Assert.Equal(6, drawn.Count(d => d.Brush == Blue));
    }

    [Fact]
    public void A_selection_hides_the_hint()
    {
        Assert.DoesNotContain(Drawn("/model ", box => box.Select(0, 3)), d => d.Brush == Grey);
    }
}
