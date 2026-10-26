using System;

namespace StardewMCP.Agent.Brain;

/// <summary>
/// The built-in AI's own brake on a tool that keeps failing: the mod's loop guard does not see instant tools, and on the
/// first morning of the 14B run AI2 called name 20 times in a row (no naming box open) while the world stood frozen for
/// everyone. After <see cref="Warn"/> failures in a row of the same tool the result says to stop; from <see cref="Block"/>
/// on the call is not run until another tool is called or the same one succeeds. Game-free, so the unit tests compile it.
/// </summary>
public sealed class RepeatBrake
{
    public const int Warn = 3, Block = 5;

    private string _tool = "";
    private int _fails;

    /// <summary>A refusal for this call, or null to run it.</summary>
    public string? Before(string tool) =>
        tool == _tool && _fails >= Block
            ? $"Not run: {tool} failed {_fails} times in a row. Do something else first (status, look, or another tool), then try again only if the situation changed."
            : null;

    /// <summary>Record the result; returns a line to add to it ("" when nothing to say).</summary>
    public string After(string tool, bool ok)
    {
        if (ok)
        {
            if (tool == _tool) { _tool = ""; _fails = 0; }
            return "";
        }
        if (tool == _tool) _fails++;
        else { _tool = tool; _fails = 1; }
        return _fails >= Warn
            ? $"\n{tool} has failed {_fails} times in a row. Stop calling it: read the result, call status, then choose a different action."
            : "";
    }

    /// <summary>Another tool was called: the streak of the failing one is broken.</summary>
    public void Other(string tool)
    {
        if (tool != _tool) { _tool = ""; _fails = 0; }
    }
}
