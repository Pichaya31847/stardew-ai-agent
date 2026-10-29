using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class GeodeGuideTests
{
    private static readonly string[] Carried = { "Geode", "Frozen Geode", "Omni Geode" };

    [Theory]
    [InlineData("Geode", 0)]
    [InlineData("geode", 0)]
    [InlineData("Normal Geode", 0)]
    [InlineData("geodes", 0)]
    [InlineData("frozen", 1)]
    [InlineData("Frozen Geodes", 1)]
    [InlineData("omni geode", 2)]
    [InlineData("Magma Geode", -1)]
    [InlineData("", -1)]
    public void Loose_names_find_the_carried_geode(string query, int expected) =>
        Assert.Equal(expected, GeodeGuide.Match(Carried, query));

    [Fact]
    public void Every_kind_says_where_it_comes_from_and_what_it_holds()
    {
        Assert.Equal(8, GeodeGuide.Kinds.Count);
        foreach (var k in GeodeGuide.Kinds)
        {
            Assert.StartsWith("(O)", k.Id);
            Assert.NotEmpty(k.Where);
            Assert.NotEmpty(k.Gives);
        }
        Assert.Equal("Artifact Trove", GeodeGuide.For("(O)275")!.Name);
        Assert.Null(GeodeGuide.For("(O)128"));
    }
}
