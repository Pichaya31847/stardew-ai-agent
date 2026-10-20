using StardewMCP.Agent;
using Xunit;

public class BubbleTextTests
{
    [Fact]
    public void ShortTextIsOneLine() =>
        Assert.Equal(new[] { "hi there" }, BubbleText.Wrap("hi there", 20, 3));

    [Fact]
    public void BreaksAtASpace() =>
        Assert.Equal(new[] { "hello big", "world" }, BubbleText.Wrap("hello big world", 12, 3));

    [Fact]
    public void LongTextEndsInEllipsis()
    {
        var lines = BubbleText.Wrap("aaaa bbbb cccc dddd", 4, 2);
        Assert.Equal(new[] { "aaaa", "bbbb…" }, lines);
    }

    [Fact]
    public void ThaiMarksStayOnTheirLetterAndLeadingVowelsOnTheNext()
    {
        // ที่ = ท + ี + ่ (two marks); เ leads the letter after it
        var lines = BubbleText.Wrap("ที่นาเรา", 3, 3);
        Assert.Equal("ที่นาเรา", string.Concat(lines));
        foreach (var line in lines)
        {
            Assert.False(line.EndsWith("เ"));
            Assert.False(line.Length > 0 && (line[0] == 'ี' || line[0] == '่'));
        }
        Assert.Equal("ที่นา", lines[0]);
    }
}
