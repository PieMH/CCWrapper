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

    [Fact]
    public void StartsWithCommand_wants_the_first_word_to_be_a_known_name()
    {
        SlashCommand[] known = [new("compact", "", "t"), new("my:cmd", "", "t")];
        Assert.True(SlashWord.StartsWithCommand("/compact", known));
        Assert.True(SlashWord.StartsWithCommand("/COMPACT now\nmore", known));
        Assert.True(SlashWord.StartsWithCommand("/my:cmd arg", known));
        Assert.False(SlashWord.StartsWithCommand("/compa", known));
        Assert.False(SlashWord.StartsWithCommand("/", known));
        Assert.False(SlashWord.StartsWithCommand(" /compact", known));
        Assert.False(SlashWord.StartsWithCommand("run /compact", known));
    }
}
