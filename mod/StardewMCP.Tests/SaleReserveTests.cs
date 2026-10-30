using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class SaleReserveTests
{
    [Fact]
    public void PlantingInputsCannotBeSoldByDefaultButProduceCan()
    {
        Assert.Equal(0, SaleReserve.Available("(O)481", 20, true, false));
        Assert.Equal(0, SaleReserve.Available("(O)368", 10, true, false));
        Assert.Equal(20, SaleReserve.Available("(O)258", 20, false, false));
        Assert.Equal(20, SaleReserve.Available("(O)481", 20, true, true));
    }

    [Theory]
    [InlineData("(O)388")] // Wood
    [InlineData("(O)390")] // Stone
    [InlineData("(O)771")] // Fiber
    [InlineData("(O)382")] // Coal
    [InlineData("(O)378")] // Copper Ore
    [InlineData("(O)334")] // Copper Bar
    [InlineData("(O)92")]  // Sap
    public void BasicResourcesAreNeverSold(string id)
    {
        Assert.True(SaleReserve.IsBasicResource(id));
        Assert.Equal(0, SaleReserve.Available(id, 500, false, false));
        Assert.Equal(0, SaleReserve.Available(id, 500, false, true));
    }
}
