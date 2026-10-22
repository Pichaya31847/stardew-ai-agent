using System.Text.Json;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class LoopDetectorTests
{
    private LoopDetector _detector;
    private int _day;

    public LoopDetectorTests()
    {
        _day = 1;
        _detector = new LoopDetector(hintAfter: 3, blockAfter: 5, noProgressAfter: 4, () => _day);
    }

    // ------ repeat failure

    [Fact]
    public void Same_call_failing_several_times_adds_a_hint_and_refuses_it_later()
    {
        var key = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);

        // 1st failure
        Assert.Null(_detector.Before(key));
        var notes = _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap1");
        Assert.Empty(notes);

        // 2nd failure
        Assert.Null(_detector.Before(key));
        notes = _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap2");
        Assert.Empty(notes);

        // 3rd failure - gets hint
        Assert.Null(_detector.Before(key));
        notes = _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap3");
        Assert.Single(notes);
        Assert.Equal(GuardKind.Repeat, notes[0].Kind);
        Assert.Equal(3, notes[0].Count);

        // 4th failure - gets hint with block warning
        Assert.Null(_detector.Before(key));
        notes = _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap4");
        Assert.Single(notes);
        Assert.Equal(GuardKind.Repeat, notes[0].Kind);
        Assert.Equal(4, notes[0].Count);
        Assert.Contains("The next identical attempt will not be run.", notes[0].Text);

        // 5th attempt - refused before running
        var note = _detector.Before(key);
        Assert.NotNull(note);
        Assert.Equal(GuardKind.Refused, note.Kind);
        Assert.Equal(4, note.Count);
        Assert.Contains("Do something else first", note.Text);

        // After call handles the refusal correctly
        notes = _detector.After(key, instant: false, ok: false, result: note.Text, snapshot: "snap4");
        Assert.Empty(notes); // Because it was refused, After doesn't generate *new* notes
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var key = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);

        // 1st failure
        _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap1");

        // 2nd failure
        _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap1");

        // Success
        _detector.After(key, instant: false, ok: true, result: "Moved.", snapshot: "snap2");

        // 1st failure after success
        var notes = _detector.After(key, instant: false, ok: false, result: "Cannot move there.", snapshot: "snap2");
        Assert.Empty(notes); // If count wasn't reset, this would be the 3rd failure and get a hint
    }

    // ------ no progress

    [Fact]
    public void Many_calls_in_a_row_that_change_nothing_trigger_no_progress_note()
    {
        var key1 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);
        var key2 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"down\"}").RootElement);

        // 1st call, no progress
        var notes = _detector.After(key1, instant: false, ok: false, result: "Failed.", snapshot: "snap1");
        Assert.Empty(notes);

        // 2nd call, no progress
        notes = _detector.After(key2, instant: false, ok: false, result: "Failed.", snapshot: "snap1");
        Assert.Empty(notes);

        // 3rd call, no progress
        notes = _detector.After(key1, instant: false, ok: false, result: "Failed.", snapshot: "snap1");
        Assert.Empty(notes);

        // 4th call, no progress
        notes = _detector.After(key2, instant: false, ok: false, result: "Failed.", snapshot: "snap1");
        Assert.Empty(notes);

        // 5th call, no progress - triggers note!
        notes = _detector.After(key1, instant: false, ok: false, result: "Failed.", snapshot: "snap1");
        Assert.Single(notes);
        Assert.Equal(GuardKind.NoProgress, notes[0].Kind);
        Assert.Equal(4, notes[0].Count);
    }

    // ------ key of

    [Fact]
    public void KeyOf_gives_equal_keys_for_equal_calls_and_different_keys_for_different_args()
    {
        var key1 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);
        var key2 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);
        var key3 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"down\"}").RootElement);
        var key4 = LoopDetector.KeyOf("use_tool", JsonDocument.Parse("{\"dir\":\"up\"}").RootElement);

        // Argument order does not matter
        var key5 = LoopDetector.KeyOf("build", JsonDocument.Parse("{\"a\":1,\"b\":2}").RootElement);
        var key6 = LoopDetector.KeyOf("build", JsonDocument.Parse("{\"b\":2,\"a\":1}").RootElement);

        // Free-text arguments (reason, diary) differ on every call and are left out of the key
        var key7 = LoopDetector.KeyOf("move", JsonDocument.Parse("{\"dir\":\"up\",\"reason\":\"to the farm\"}").RootElement);

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key1, key4);
        Assert.Equal(key5, key6);
        Assert.Equal(key1, key7);
    }
}
