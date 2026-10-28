using CCWrapper.Data;
using CCWrapper.Views;

namespace CCWrapper.Tests.Input;

public sealed class SlashWordTests
{
    [Theory]
    [InlineData("/com", 4, 0, 4)] // at the start, caret at the end
    [InlineData("/com", 1, 0, 4)] // right after the "/"
    [InlineData("/com rest", 2, 0, 4)]
    [InlineData("run /com now", 8, 4, 8)] // after a space
    [InlineData("a\n/co", 5, 2, 5)] // after a newline
    [InlineData("a\t/co", 4, 2, 5)] // after a tab
    public void At_finds_the_word_the_caret_is_in(string text, int caret, int start, int end)
    {
        Assert.Equal((start, end), SlashWord.At(text, caret));
    }

    [Theory]
    [InlineData("a/b", 3)] // inside a path
    [InlineData("see C:/dir/file", 10)]
    [InlineData("https://x.y/z", 13)]
    [InlineData("/com ", 5)] // caret after the word, past the space
    [InlineData("run /com now", 12)]
    [InlineData("/com", 0)] // before the "/"
    [InlineData("run /com", 4)]
    [InlineData("plain text", 5)]
    [InlineData("", 0)]
    public void Not_a_word(string text, int caret) => Assert.Null(SlashWord.At(text, caret));

    static readonly SlashCommand[] Known = [new("compact", "", "t", "[instructions]"), new("my:cmd", "", "t"), new("model", "", "t", "[model]")];

    [Fact]
    public void Commands_finds_known_names_anywhere_but_not_paths_urls_or_unknown_words()
    {
        Assert.Equal([(0, 8)], SlashWord.Commands("/COMPACT now", Known).Select(c => (c.Start, c.End)));
        Assert.Equal([(4, 11), (16, 22)], SlashWord.Commands("run /my:cmd and\n/model x", Known).Select(c => (c.Start, c.End)));
        Assert.Equal("compact", SlashWord.Commands("x\t/compact", Known).Single().Command.Name);
        Assert.Empty(SlashWord.Commands("/compa /foo a/compact https://x/model /compact/x /", Known));
        Assert.Empty(SlashWord.Commands("", Known));
    }

    [Theory]
    [InlineData("/compact", 8, " [instructions]")] // no space yet: the whole hint after one
    [InlineData("/compact ", 9, "[instructions]")]
    [InlineData("/compact [ins", 13, "tructions]")] // typed arguments are its start: the rest
    [InlineData("/compact [INS", 13, "tructions]")]
    [InlineData("run /model", 10, " [model]")] // anywhere in the prompt
    [InlineData("a\n/model \nb", 9, "[model]")] // the caret ends its line
    [InlineData("/compact x /model ", 18, "[model]")] // the last command of the line
    public void Ghost_shows_the_rest_of_the_hint(string text, int caret, string ghost) => Assert.Equal(ghost, SlashWord.Ghost(text, caret, Known));

    [Theory]
    [InlineData("/compact now", 12)] // differs from the hint
    [InlineData("/compact [instructions]", 23)] // fills it
    [InlineData("/compact [ins more", 13)] // text after the caret on its line
    [InlineData("/my:cmd ", 8)] // no hint
    [InlineData("/compa", 6)] // not a known command
    [InlineData("a/model ", 8)]
    [InlineData("/model\nx", 8)] // the command is on another line
    [InlineData("/model ", 0)]
    [InlineData("", 0)]
    public void No_ghost(string text, int caret) => Assert.Null(SlashWord.Ghost(text, caret, Known));
}
