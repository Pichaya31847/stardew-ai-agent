using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP.Agent;

/// <summary>One line in the run log / overlay feed.</summary>
public class LogEntry
{
    public long Seq { get; set; }
    public string Ts { get; set; } = "";
    public int Day { get; set; }
    public string Date { get; set; } = "";
    public string GameTime { get; set; } = "";
    public string Location { get; set; } = "";
    /// <summary>think | action | result | diary | system</summary>
    public string Kind { get; set; } = "";
    public string? Tool { get; set; }
    public string? Text { get; set; }
    public object? Args { get; set; }
    public bool? Ok { get; set; }
    public long? Ms { get; set; }
    /// <summary>Player money when the entry was written (overlay's day-card delta).</summary>
    public int? Money { get; set; }
}

/// <summary>Writes the proof-of-run log (JSONL per in-game day) and keeps a feed for the overlay.</summary>
public class AgentLog
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _feed = new();
    private readonly IMonitor _monitor;
    private long _seq;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Dir { get; }

    /// <summary>Free-form metadata posted by the runner (model, session id...). Shown on the overlay.</summary>
    public Dictionary<string, string> Meta { get; } = new();

    public AgentLog(string dir, IMonitor monitor)
    {
        Dir = dir;
        _monitor = monitor;
        Directory.CreateDirectory(Dir);
        _seq = DateTime.UtcNow.Ticks / 10000; // monotonic across restarts
    }

    /// <summary>Days since the farm started, 1-based (spring 1 year 1 = day 1).</summary>
    public static int FarmDay => Context.IsWorldReady ? Game1.Date.TotalDays + 1 : 0;

    public LogEntry Add(string kind, string? tool = null, string? text = null, object? args = null, bool? ok = null, long? ms = null)
    {
        var e = new LogEntry
        {
            Ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            Day = FarmDay,
            Date = Context.IsWorldReady ? $"{Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}" : "",
            GameTime = Context.IsWorldReady ? WorldText.FormatTime(Game1.timeOfDay) : "",
            Location = Game1.currentLocation?.Name ?? "",
            Kind = kind,
            Tool = tool,
            Text = text,
            Args = args,
            Ok = ok,
            Ms = ms,
            Money = Context.IsWorldReady ? Game1.player.Money : null
        };

        lock (_lock)
        {
            e.Seq = ++_seq;
            _feed.Add(e);
            if (_feed.Count > 500)
                _feed.RemoveRange(0, _feed.Count - 500);
            try
            {
                File.AppendAllText(DayFile(e.Day), JsonSerializer.Serialize(e, Json) + "\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Log write failed: {ex.Message}", LogLevel.Warn);
            }
        }
        return e;
    }

    public List<LogEntry> FeedAfter(long seq)
    {
        lock (_lock)
            return _feed.Where(e => e.Seq > seq).ToList();
    }

    public string DayFile(int day) => Path.Combine(Dir, $"day_{day:000}.jsonl");

    public string DiaryFile(int day) => Path.Combine(Dir, $"diary_{day:000}.md");

    public void WriteDiary(int day, string text)
    {
        var header = $"# Day {day} — {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}\n\n";
        File.WriteAllText(DiaryFile(day), header + text.Trim() + "\n", Encoding.UTF8);
        Add("diary", text: text);
    }

    public List<int> Days()
    {
        return Directory.GetFiles(Dir, "day_*.jsonl")
            .Select(f => Path.GetFileNameWithoutExtension(f).Substring(4))
            .Select(s => int.TryParse(s, out var d) ? d : 0)
            .Where(d => d > 0)
            .OrderBy(d => d)
            .ToList();
    }
}
