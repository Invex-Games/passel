using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class ParcelFileNameTests
{
    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("a/b\\c.png", "c.png")]
    [InlineData("C:\\Users\\me\\Pictures\\holiday.jpg", "holiday.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("Quarterly report (final).pdf", "Quarterly report (final).pdf")]
    [InlineData("\u65e5\u672c\u8a9e.txt", "\u65e5\u672c\u8a9e.txt")]
    public void A_name_keeps_its_last_segment(string given, string expected)
    {
        Assert.Equal(expected, ParcelFileName.Sanitize(given));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("dir/")]
    [InlineData("   ")]
    [InlineData(" . . ")]
    [InlineData("\u202E\u0000\r\n")]
    public void A_name_with_nothing_left_is_none(string? given)
    {
        Assert.Null(ParcelFileName.Sanitize(given));
    }

    /// <summary>
    ///     A right-to-left override makes <c>photo&#x202E;gnp.exe</c> display as a PNG.
    /// </summary>
    [Theory]
    [InlineData("photo\u202Egnp.exe", "photognp.exe")]
    [InlineData("a\u202Ab\u202Bc\u202Cd\u202De", "abcde")]
    [InlineData("a\u2066b\u2067c\u2068d\u2069e", "abcde")]
    [InlineData("a\u200Eb\u200Fc\u061Cd", "abcd")]
    [InlineData("\uFEFFname.txt", "name.txt")]
    public void Bidirectional_controls_are_removed(string given, string expected)
    {
        Assert.Equal(expected, ParcelFileName.Sanitize(given));
    }

    [Theory]
    [InlineData("a\rb\nc.txt", "abc.txt")]
    [InlineData("a\u0000b\u0007c\u001Fd\u007Fe.txt", "abcde.txt")]
    [InlineData("tab\there.txt", "tabhere.txt")]
    [InlineData("a\u2028b\u2029c.txt", "abc.txt")]
    [InlineData("a\u0085b.txt", "ab.txt")]
    public void Control_characters_and_line_separators_are_removed(string given, string expected)
    {
        Assert.Equal(expected, ParcelFileName.Sanitize(given));
    }

    [Theory]
    [InlineData("  photo.png  ", "photo.png")]
    [InlineData("photo.png.", "photo.png")]
    [InlineData("...photo.png", "photo.png")]
    [InlineData("\u00A0photo.png\u3000", "photo.png")]
    [InlineData("my  photo . png", "my  photo . png")]
    public void Spaces_and_dots_at_either_end_are_trimmed(string given, string expected)
    {
        Assert.Equal(expected, ParcelFileName.Sanitize(given));
    }

    [Fact]
    public void A_name_is_normalised_to_composed_form()
    {
        // An e followed by a combining acute accent
        Assert.Equal("caf\u00E9.txt", ParcelFileName.Sanitize("cafe\u0301.txt"));
    }

    /// <summary>
    ///     Emoji are pairs of UTF-16 halves, and some are joined with a zero-width joiner, which is a format character
    ///     too; neither is taken for something to remove.
    /// </summary>
    [Fact]
    public void Emoji_survive_whole()
    {
        const string family = "\uD83D\uDC69\u200D\uD83D\uDC67 photo.jpg";

        Assert.Equal(family, ParcelFileName.Sanitize(family));
    }

    [Theory]
    [InlineData("a\uD800b.txt", "ab.txt")]
    [InlineData("a\uDC00b.txt", "ab.txt")]
    public void A_lone_surrogate_is_removed(string given, string expected)
    {
        Assert.Equal(expected, ParcelFileName.Sanitize(given));
    }

    [Fact]
    public void The_length_is_left_to_the_caller()
    {
        var name = new string('a', 300) + ".txt";

        Assert.Equal(name, ParcelFileName.Sanitize(name));
    }
}
