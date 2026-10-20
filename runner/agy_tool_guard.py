"""agy PreToolUse hook: only this seat's stardew MCP server may be called; every other tool is denied before it runs.

run_days.write_agy_guard puts <workdir>/.agents/hooks.json and a .cmd that runs this with the seat's server name.
agy pipes the tool call as JSON on stdin ({"toolCall": {"name": ..., "args": {...}}}) and reads {"decision": ...} from
stdout. Without it agy runs with --dangerously-skip-permissions, so a shell command (run_command) would execute on the
host before the runner could see and kill it. Standard library only; anything unreadable is denied.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

DENY_REASON = "Only the Stardew game tools are available in this session. Use them, or end your turn."


def own_mcp_file(path: str, server: str) -> bool:
    """agy reads its copy of the server's instructions under ~/.gemini/antigravity-cli/mcp/<server>/ with view_file."""
    if not path or not server:
        return False
    root = (Path.home() / ".gemini" / "antigravity-cli" / "mcp" / server).resolve()
    try:
        return Path(path).resolve().is_relative_to(root)
    except (OSError, ValueError):
        return False


def own_tool_output(path: str) -> bool:
    """agy saves a long tool result under ~/.gemini/antigravity-cli/brain/<conversation>/.system_generated/ and reads it back."""
    if not path:
        return False
    root = (Path.home() / ".gemini" / "antigravity-cli" / "brain").resolve()
    try:
        rel = Path(path).resolve().relative_to(root)
    except (OSError, ValueError):
        return False
    return len(rel.parts) > 2 and rel.parts[1] == ".system_generated"


def readable(path: str, server: str) -> bool:
    return own_mcp_file(path, server) or own_tool_output(path)


# agy reads files with view_file (AbsolutePath) or read_file (Path); read_file of its own saved tool output was taken for a
# forbidden tool and Gemini sat out most of day 7 (2026-10-20)
READERS = {"view_file": "AbsolutePath", "read_file": "Path"}


def read_path(name: str, args: dict) -> str | None:
    """The file a read tool call opens, or None when the call is not a file read."""
    key = READERS.get(name)
    return str(args.get(key) or args.get("AbsolutePath") or args.get("Path") or "") if key else None


def decide(call: dict, server: str) -> str:
    name = str(call.get("name") or "")
    args = call.get("args") if isinstance(call.get("args"), dict) else {}
    if name == "call_mcp_tool":
        srv = str(args.get("ServerName") or args.get("server_name") or args.get("server") or "")
        return "allow" if server and srv.lower() == server.lower() else "deny"
    path = read_path(name, args)
    if path is not None and readable(path, server):
        return "allow"
    return "deny"


def main(argv: list[str]) -> int:
    server = argv[1] if len(argv) > 1 else ""
    try:
        call = json.loads(sys.stdin.read() or "{}").get("toolCall") or {}
        decision = decide(call, server)
    except Exception:
        decision = "deny"
    out = {"decision": decision}
    if decision == "deny":
        out["reason"] = DENY_REASON
    print(json.dumps(out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
