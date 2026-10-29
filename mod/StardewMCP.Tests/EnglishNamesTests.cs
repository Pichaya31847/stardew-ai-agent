using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class EnglishNamesTests
{
    private static NameMap Map()
    {
        var m = new NameMap();
        m.Add("เลวิส", "Lewis");
        m.Add("เอเวอลิน", "Evelyn");
        m.Add("ไข่", "Egg");
        m.Add("ไข่สีน้ำตาล", "Brown Egg");
        return m;
    }

    [Fact]
    public void Names_become_english_keys()
    {
        var m = Map();
        Assert.Equal("Talking to Lewis (friendship 1 hearts).", m.Apply("Talking to เลวิส (friendship 1 hearts)."));
        Assert.Equal("talk_to(name=\"Evelyn\")", m.Apply("talk_to(name=\"เอเวอลิน\")"));
    }

    [Fact]
    public void Longest_name_wins_and_words_are_spaced()
    {
        var m = Map();
        Assert.Equal("2 x Brown Egg", m.Apply("2 x ไข่สีน้ำตาล"));
        Assert.Equal("คุยกับ Lewis แล้ว", m.Apply("คุยกับเลวิสแล้ว"));
    }

    [Fact]
    public void English_text_and_bad_pairs_are_left_alone()
    {
        var m = Map();
        m.Add("Lewis", "Lewis");    // nothing to translate
        m.Add("ก", "A");            // one letter: would eat Thai words
        m.Add("ปิแอร์", "ปิแอร์");    // no English side
        Assert.Equal(4, m.Count);
        Assert.Equal("Lewis gave 2 Egg.", m.Apply("Lewis gave 2 Egg."));
        Assert.Equal("", m.Apply(null));
    }
}
