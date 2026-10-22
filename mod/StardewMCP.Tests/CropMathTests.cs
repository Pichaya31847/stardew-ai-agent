using System;
using Xunit;
using StardewMCP.Agent;

namespace StardewMCP.Tests;

public class CropMathTests
{
    private static readonly bool[] SpringOnly = { true, false, false, false };
    private static readonly bool[] SummerFall = { false, true, true, false };
    private const int Spring = 0, Summer = 1, Fall = 2, Winter = 3;

    // ------------------------------------------------------------------ seasons

    [Theory]
    [InlineData("spring", 0)]
    [InlineData("Summer", 1)]
    [InlineData(" FALL ", 2)]
    [InlineData("autumn", 2)]
    [InlineData("winter", 3)]
    public void Season_names(string name, int expected)
    {
        Assert.Equal(expected, CropMath.SeasonIndex(name));
    }

    [Theory]
    [InlineData("monsoon")]
    [InlineData(null)]
    public void Invalid_season_names(string? name)
    {
        Assert.Equal(-1, CropMath.SeasonIndex(name));
    }

    [Fact]
    public void Season_name_returns_string()
    {
        Assert.Equal("fall", CropMath.SeasonName(2));
    }

    [Theory]
    [InlineData(new int[] { 1, 2, 4, 4, 1 }, 12)]
    [InlineData(new int[] { 1, 1, 1, 1 }, 4)]
    [InlineData(new int[] { }, 0)]
    [InlineData(new int[] { 2, -1, 3 }, 5)] // bad data is ignored, not subtracted
    public void Growth_days_is_the_sum_of_the_phases(int[] phases, int expected)
    {
        Assert.Equal(expected, CropMath.GrowthDays(phases));
    }

    // ------------------------------------------------------------------ maturity boundary

    [Fact]
    public void Twelve_days_left_is_the_boundary_for_a_12_day_crop()
    {
        var ok = CropMath.Plan(Spring, 16, SpringOnly, 12, 0);
        Assert.True(ok.Matures);
        Assert.Equal(12, ok.DaysAvailable);
        Assert.Equal(16, ok.LastPlantDay);
        Assert.Equal(1, ok.Harvests);
        Assert.Equal((Spring, 28), CropMath.Advance(Spring, 16, 12));

        var late = CropMath.Plan(Spring, 16, SpringOnly, 13, 0);
        Assert.False(late.Matures);
        Assert.Equal(15, late.LastPlantDay);
        Assert.Equal(0, late.Harvests);
        Assert.Equal(0, late.Plantings);
        Assert.Equal(0, late.TileDays);
    }

    [Fact]
    public void One_day_later_the_12_day_crop_is_too_late()
    {
        Assert.False(CropMath.Plan(Spring, 17, SpringOnly, 12, 0).Matures);
    }

    [Fact]
    public void Spring_20_has_eight_days_left()
    {
        Assert.Equal(8, CropMath.DaysAvailable(Spring, 20, SpringOnly));
        Assert.False(CropMath.Plan(Spring, 20, SpringOnly, 12, 0).Matures);
        Assert.True(CropMath.Plan(Spring, 20, SpringOnly, 4, 0).Matures);
        Assert.True(CropMath.Plan(Spring, 20, SpringOnly, 6, 0).Matures);
        Assert.False(CropMath.Plan(Spring, 20, SpringOnly, 10, 3).Matures);
    }

    [Fact]
    public void Planting_on_the_last_day_never_matures()
    {
        Assert.Equal(0, CropMath.DaysAvailable(Spring, 28, SpringOnly));
        Assert.False(CropMath.Plan(Spring, 28, SpringOnly, 1, 0).Matures);
    }

    [Fact]
    public void Out_of_season_crop_has_no_days()
    {
        var p = CropMath.Plan(Summer, 1, SpringOnly, 4, 0);
        Assert.False(p.InSeason);
        Assert.False(p.Matures);
        Assert.Equal(0, p.DaysAvailable);
    }

    [Fact]
    public void Bad_growth_data_gives_no_plan()
    {
        Assert.False(CropMath.Plan(Spring, 1, SpringOnly, 0, 0).Matures);
    }

    // ------------------------------------------------------------------ harvest count

    [Fact]
    public void Regrowing_crop_counts_every_regrow()
    {
        var p = CropMath.Plan(Spring, 1, SpringOnly, 10, 3);
        Assert.Equal(27, p.DaysAvailable);
        Assert.Equal(6, p.Harvests);
        Assert.Equal(1, p.Plantings);
        Assert.Equal(10 + 5 * 3, p.TileDays);
    }

    [Fact]
    public void Regrowing_crop_planted_late_gets_fewer_harvests()
    {
        var p = CropMath.Plan(Spring, 16, SpringOnly, 10, 3);
        Assert.True(p.Matures);
        Assert.Equal(1, p.Harvests);
        Assert.Equal(18, p.LastPlantDay);
    }

    [Fact]
    public void Single_harvest_crop_replants_back_to_back()
    {
        var p = CropMath.Plan(Spring, 1, SpringOnly, 4, 0);
        Assert.Equal(27, p.DaysAvailable);
        Assert.Equal(1, p.Harvests);
        Assert.Equal(6, p.Plantings);
        Assert.Equal(24, p.TileDays);
    }

    [Fact]
    public void First_harvest_day_rolls_into_the_next_season()
    {
        Assert.Equal((Summer, 4), CropMath.Advance(Spring, 20, 12));
        Assert.Equal((Spring, 5), CropMath.Advance(Spring, 1, 4));
        Assert.Equal((Winter, 4), CropMath.Advance(Fall, 20, 12));
        Assert.Equal((4, 4), CropMath.Advance(Winter, 20, 12)); // season 4 = next year's spring
    }

    // ------------------------------------------------------------------ multi season

    [Fact]
    public void Crop_that_grows_in_two_seasons_keeps_growing_across_the_change()
    {
        var p = CropMath.Plan(Summer, 20, SummerFall, 14, 0);
        Assert.Equal(36, p.DaysAvailable);
        Assert.True(p.Matures);
        Assert.True(p.LastPlantDay >= CropMath.DaysPerSeason);
        Assert.Equal((Fall, 6), CropMath.Advance(Summer, 20, 14));
    }

    [Fact]
    public void Same_crop_in_its_last_season_is_limited_to_that_season()
    {
        var p = CropMath.Plan(Fall, 20, SummerFall, 14, 0);
        Assert.Equal(8, p.DaysAvailable);
        Assert.False(p.Matures);
    }

    [Fact]
    public void Growth_does_not_continue_into_a_season_the_crop_skips()
    {
        Assert.Equal(27, CropMath.DaysAvailable(Spring, 1, new[] { true, false, true, false }));
    }

    [Fact]
    public void Growth_does_not_wrap_past_winter()
    {
        Assert.Equal(27, CropMath.DaysAvailable(Winter, 1, new[] { true, true, true, true }));
    }

    // ------------------------------------------------------------------ money

    [Theory]
    [InlineData(35, 0, 35)]
    [InlineData(35, 1, 43)]
    [InlineData(35, 2, 52)]
    [InlineData(35, 4, 70)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 1, 125)]
    [InlineData(100, 2, 150)]
    [InlineData(100, 4, 200)]
    public void Price_by_quality(int basePrice, int quality, int expected)
    {
        Assert.Equal(expected, CropMath.PriceAtQuality(basePrice, quality));
    }

    [Theory]
    [InlineData(1, 1, 0.0, 1.0)]
    [InlineData(3, 3, 0.0, 3.0)]
    [InlineData(1, 3, 0.0, 2.0)]
    [InlineData(1, 1, 0.2, 1.2)]
    [InlineData(0, 0, 0.0, 1.0)] // bad data still gives one item
    public void Expected_yield(int minStack, int maxStack, double extraChance, double expected)
    {
        Assert.Equal(expected, CropMath.ExpectedYield(minStack, maxStack, extraChance), 5);
    }

    [Fact]
    public void Profit_of_a_single_harvest_crop_is_independent_of_how_often_it_replants()
    {
        var p = CropMath.Plan(Spring, 1, SpringOnly, 4, 0);
        double net = CropMath.NetGold(p, 1.0, 35, 20);
        Assert.Equal(6.0 * (35.0 - 20.0), net, 5);
        Assert.Equal((35.0 - 20.0) / 4.0, CropMath.GoldPerTilePerDay(p, net), 5);
    }

    [Fact]
    public void Profit_of_a_regrowing_crop_pays_for_the_seed_once()
    {
        var p = CropMath.Plan(Spring, 1, SpringOnly, 10, 3);
        double net = CropMath.NetGold(p, 1.0, 40, 60);
        Assert.Equal(6.0 * 40.0 - 60.0, net, 5);
        Assert.Equal((6.0 * 40.0 - 60.0) / 25.0, CropMath.GoldPerTilePerDay(p, net), 5);
    }

    [Fact]
    public void Crop_that_will_not_mature_earns_nothing()
    {
        var p = CropMath.Plan(Spring, 20, SpringOnly, 12, 0);
        Assert.Equal(0.0, CropMath.NetGold(p, 1.0, 175, 80), 5);
        Assert.Equal(0.0, CropMath.GoldPerTilePerDay(p, 0.0), 5);
    }

    [Fact]
    public void Stack_of_three_triples_the_income()
    {
        var p = CropMath.Plan(Summer, 1, new[] { false, true, false, false }, 13, 4);
        Assert.Equal(3.0 * p.Harvests * 50.0, CropMath.NetGold(p, 3.0, 50, 0), 5);
    }

    [Theory]
    [InlineData(20, 10, 20, false)]
    [InlineData(-1, 10, 20, true)] // no listed price: twice the base value
    [InlineData(0, 0, 0, true)]
    public void Shop_price(int listedPrice, int objectBasePrice, int expectedPrice, bool expectedEstimated)
    {
        var (price, estimated) = CropMath.ShopPrice(listedPrice, objectBasePrice);
        Assert.Equal(expectedPrice, price);
        Assert.Equal(expectedEstimated, estimated);
    }

    // ------------------------------------------------------------------ item id

    [Theory]
    [InlineData("24", "(O)24")]
    [InlineData("(O)24", "(O)24")]
    [InlineData("(BC)13", "(BC)13")]
    public void Qualify(string input, string expected)
    {
        Assert.Equal(expected, CropMath.Qualify(input));
    }

    [Theory]
    [InlineData("(O)24", "24")]
    [InlineData("(o)24", "24")]
    [InlineData("24", "24")]
    [InlineData("(BC)13", "(BC)13")]
    [InlineData(null, "")]
    public void Unqualify(string? input, string expected)
    {
        Assert.Equal(expected, CropMath.Unqualify(input));
    }

    [Theory]
    [InlineData("(O)472", "472", true)]
    [InlineData("472", "(O)472", true)]
    [InlineData("(O)472", "(O)473", false)]
    [InlineData("(BC)13", "(O)13", false)]
    [InlineData(null, "472", false)]
    [InlineData("", "", false)]
    public void Same_item_ignores_the_object_prefix(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, CropMath.SameItem(a, b));
    }

    // ------------------------------------------------------------------ recipe

    private const string CRAFT = "388 2 390 1/Home/322/false/default/";
    private const string COOK = "-5 1 245 1/10 10/194/default/";

    [Theory]
    [InlineData(CRAFT, "388", 0, 2, false)]
    [InlineData(CRAFT, "(O)390", 0, 1, false)]
    [InlineData(CRAFT, "389", 0, 0, false)]
    public void Direct_ingredient(string recipe, string itemId, int category, int expectedCount, bool expectedViaCategory)
    {
        var (count, viaCategory) = RecipeMath.IngredientCount(recipe, itemId, category);
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedViaCategory, viaCategory);
    }

    [Theory]
    [InlineData(COOK, "176", -5, 1, true)] // -5 means any egg: used via its category
    [InlineData(COOK, "245", -5, 1, false)] // the direct match wins
    [InlineData(COOK, "176", 0, 0, false)]
    [InlineData(COOK, "176", -6, 0, false)]
    public void Category_ingredient(string recipe, string itemId, int category, int expectedCount, bool expectedViaCategory)
    {
        var (count, viaCategory) = RecipeMath.IngredientCount(recipe, itemId, category);
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedViaCategory, viaCategory);
    }

    [Fact]
    public void Item_id_is_not_confused_with_a_longer_id()
    {
        var (count, viaCategory) = RecipeMath.IngredientCount("1388 5", "388", 0);
        Assert.Equal(0, count);
        Assert.False(viaCategory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Home/1")]
    [InlineData("388")]
    [InlineData("388 x")]
    [InlineData("388 0")]
    [InlineData("388 -2")]
    public void Broken_lines_are_ignored(string? bad)
    {
        var (count, viaCategory) = RecipeMath.IngredientCount(bad, "388", 0);
        Assert.Equal(0, count);
        Assert.False(viaCategory);
    }
}
