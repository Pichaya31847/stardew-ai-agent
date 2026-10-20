using System.Collections.Generic;
using System.Linq;

namespace StardewMCP.Agent;

/// <summary>
/// Lines said aloud, shown one after another per place in the order they were said, each for its own time counted only
/// while the world runs (game-free, tested). Talk happens while the world is frozen for the listeners' thinking, and the
/// recording cuts frozen time, so a line timed in real time would be gone before anyone saw it.
/// </summary>
public sealed class SpeechQueue<T>
{
    private readonly List<Entry> _lines = new();
    private long _live, _last = -1;

    private sealed class Entry
    {
        public string Place = "";
        public T Item = default!;
        public int Ms;
        public long Start = -1;
    }

    public void Add(string place, T item, int ms)
    {
        lock (_lines) _lines.Add(new Entry { Place = place, Item = item, Ms = ms });
    }

    /// <summary>Moves the clock to real time <paramref name="nowMs"/> (time while paused does not count), ends lines whose time is up
    /// and starts the next one of each place.</summary>
    public void Advance(long nowMs, bool paused)
    {
        lock (_lines)
        {
            if (_last >= 0 && !paused) _live += nowMs - _last;
            _last = nowMs;
            foreach (var place in _lines.Select(l => l.Place).Distinct().ToList())
                while (_lines.FirstOrDefault(l => l.Place == place) is { } first)
                {
                    if (first.Start < 0) { first.Start = _live; break; }
                    if (_live - first.Start < first.Ms) break;
                    _lines.Remove(first);
                }
        }
    }

    /// <summary>The line showing now in a place, if any.</summary>
    public T? Showing(string place)
    {
        lock (_lines) return _lines.FirstOrDefault(l => l.Place == place && l.Start >= 0) is { } e ? e.Item : default;
    }

    /// <summary>Every line showing now, one per place.</summary>
    public List<T> AllShowing()
    {
        lock (_lines) return _lines.Where(l => l.Start >= 0).Select(l => l.Item).ToList();
    }
}
