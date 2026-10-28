using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class LanguageGuardTests
{
    [Fact]
    public void ThaiWithAChineseWordIsSentBackNamingTheWord()
    {
        var why = LanguageGuard.Reject("อ่าน Junimo note และ捐赠 items ที่ bundle ต้องการ", "Thai", "say");
        Assert.NotNull(why);
        Assert.Contains("捐赠", why);
        Assert.Contains("Thai", why);
    }

    [Theory]
    [InlineData("สวัสดีครับ วันนี้อากาศดี")]
    [InlineData("Harvest the parsnips, then ship them.")]
    [InlineData("")]
    [InlineData(null)]
    public void TextWithoutChineseCharactersPasses(string? text) =>
        Assert.Null(LanguageGuard.Reject(text, "Thai", "think"));

    [Theory]
    [InlineData("Chinese")]
    [InlineData("zh-CN")]
    [InlineData("Japanese")]
    [InlineData("中文")]
    public void ChineseCharactersAreFineWhenTheLanguageUsesThem(string language) =>
        Assert.Null(LanguageGuard.Reject("今天去种地", language, "think"));

    [Fact]
    public void RunsAreListedOnceEach() =>
        Assert.Equal("捐赠, 农场", LanguageGuard.HanRuns("a捐赠b农场c捐赠"));
}
