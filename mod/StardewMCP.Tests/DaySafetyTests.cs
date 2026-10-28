using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class DaySafetyTests
{
    [Fact]
    public void NewDateDoesNotStartTheBrainUntilBothScreensFinishNightEvents()
    {
        Assert.True(DaySafety.CanStartDay(true, false, false, 30));
        Assert.False(DaySafety.CanStartDay(true, true, false, 31));
        Assert.False(DaySafety.CanStartDay(true, false, true, 31));
        Assert.True(DaySafety.CanStartDay(true, false, false, 31));
        Assert.False(DaySafety.CanStartDay(false, false, false, 31));
        Assert.False(DaySafety.CanStartDay(true, false, false, 0));
    }

    [Fact]
    public void TheEarlyBedRefusalNamesOneTaskAndWhenBedIsAllowedAndIsShortTheSecondTime()
    {
        var first = DaySafety.EarlyBedRefusal("10:30", 200, 270, "harvest_all()", repeat: false);
        Assert.Contains("harvest_all()", first);
        Assert.Contains("17:00", first);
        Assert.Contains("67 or less", first);
        var again = DaySafety.EarlyBedRefusal("10:40", 198, 270, "harvest_all()", repeat: true);
        Assert.Contains("harvest_all()", again);
        Assert.True(again.Length < first.Length);
    }

    [Fact]
    public void AnEmptyMessageClosesAtOnceAndAStaleOneOnlyWhileSomeoneWaitsInBed()
    {
        Assert.False(DaySafety.CloseStaleMessage(1, emptyText: true, isQuestion: false, othersWaitingInBed: false));
        Assert.True(DaySafety.CloseStaleMessage(3, emptyText: true, isQuestion: false, othersWaitingInBed: false));
        Assert.False(DaySafety.CloseStaleMessage(600, emptyText: false, isQuestion: false, othersWaitingInBed: false));
        Assert.False(DaySafety.CloseStaleMessage(10, emptyText: false, isQuestion: false, othersWaitingInBed: true));
        Assert.True(DaySafety.CloseStaleMessage(25, emptyText: false, isQuestion: false, othersWaitingInBed: true));
        Assert.False(DaySafety.CloseStaleMessage(600, emptyText: true, isQuestion: true, othersWaitingInBed: true));
    }

    [Fact]
    public void RepeatedMorningSleepIsBlockedButExhaustionAndInjuryAreAllowed()
    {
        for (int attempt = 0; attempt < 5; attempt++)
            Assert.True(DaySafety.BlockEarlyBed(600, 270, 270, 100, 100));
        Assert.False(DaySafety.BlockEarlyBed(600, 20, 270, 100, 100));
        Assert.False(DaySafety.BlockEarlyBed(600, 270, 270, 20, 100));
        Assert.False(DaySafety.BlockEarlyBed(1700, 270, 270, 100, 100));
    }

    [Fact]
    public void ATiredFarmerEarlyInACoopDayIsPointedAtARestInBed()
    {
        Assert.True(DaySafety.SuggestRest(1320, 20, 270, 100, 100, multiplayer: true));
        Assert.False(DaySafety.SuggestRest(1320, 20, 270, 100, 100, multiplayer: false)); // alone, a bed gives nothing back
        Assert.False(DaySafety.SuggestRest(1900, 20, 270, 100, 100, multiplayer: true));  // evening: sleep
        Assert.False(DaySafety.SuggestRest(1320, 200, 270, 100, 100, multiplayer: true)); // not tired
        Assert.False(DaySafety.SuggestRest(1320, 20, 270, 10, 100, multiplayer: true));   // hurt: sleep
        Assert.Contains("rest", DaySafety.LowEnergyAdvice(1320, true));
        Assert.Equal("eat something or go to bed", DaySafety.LowEnergyAdvice(2100, true));
        Assert.Equal("eat something or go to bed", DaySafety.LowEnergyAdvice(1000, false));
    }

    [Fact]
    public void SpeechCannotCancelCommittedSleepButCanInterruptAwakeWork()
    {
        Assert.False(DaySafety.CanInterruptForSocialEvent("go_to_bed"));
        Assert.True(DaySafety.CanInterruptForSocialEvent("walk_to"));
        Assert.True(DaySafety.CanInterruptForSocialEvent("rest"));
    }
}
