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
    [InlineData("(O)388", 70, 20)]
    [InlineData("(O)390", 25, 0)]
    [InlineData("(O)382", 5, 0)]
    [InlineData("(O)378", 50, 40)]
    [InlineData("(O)334", 8, 3)]
    public void OnlySurplusAboveCraftingReserveIsAvailable(string id, int inventory, int surplus)
    {
        Assert.Equal(surplus, SaleReserve.Available(id, inventory, false, false));
        Assert.Equal(inventory, SaleReserve.Available(id, inventory, false, true));
    }
}
