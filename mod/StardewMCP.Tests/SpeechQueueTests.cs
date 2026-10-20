using StardewMCP.Agent;
using Xunit;

public class SpeechQueueTests
{
    [Fact]
    public void LinesInOnePlacePlayInOrder()
    {
        var q = new SpeechQueue<string>();
        q.Add("Farm", "hi", 3000);
        q.Add("Farm", "hello", 3000);
        q.Advance(0, paused: false);
        Assert.Equal("hi", q.Showing("Farm"));
        q.Advance(2999, paused: false);
        Assert.Equal("hi", q.Showing("Farm"));
        q.Advance(3000, paused: false);
        Assert.Equal("hello", q.Showing("Farm"));
        q.Advance(6000, paused: false);
        Assert.Null(q.Showing("Farm"));
    }

    [Fact]
    public void PausedTimeDoesNotCount()
    {
        var q = new SpeechQueue<string>();
        q.Add("Farm", "hi", 3000);
        q.Advance(0, paused: true);
        q.Advance(60_000, paused: true); // a minute of thinking
        Assert.Equal("hi", q.Showing("Farm"));
        q.Advance(62_000, paused: false);
        Assert.Equal("hi", q.Showing("Farm"));
        q.Advance(63_000, paused: false);
        Assert.Null(q.Showing("Farm"));
    }

    [Fact]
    public void PlacesPlayAtTheSameTime()
    {
        var q = new SpeechQueue<string>();
        q.Add("Farm", "a", 3000);
        q.Add("Town", "b", 3000);
        q.Advance(0, paused: false);
        Assert.Equal(new[] { "a", "b" }, q.AllShowing());
    }
}
