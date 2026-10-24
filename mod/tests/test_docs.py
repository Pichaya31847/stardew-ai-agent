"""The tool reference pages (docs/tools.md and its Thai twin) list every tool the C# source defines, and no others."""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from tool_source_parser import read_all

ROOT = Path(__file__).resolve().parents[2]
AGENT_DIR = ROOT / "mod" / "StardewMCP" / "Agent"
PAGES = ["docs/tools.md", "docs/tools.th.md"]
ROW = re.compile(r"^\| `([a-z_]+)`(?:, `([a-z_]+)`)?", re.M)  # first cell of a table row: `name` or `a`, `b`


@pytest.fixture(scope="module")
def tool_names() -> set[str]:
    return {t.name for t in read_all(AGENT_DIR)}


@pytest.mark.parametrize("page", PAGES)
def test_tool_page_lists_every_tool(page, tool_names):
    listed = {n for m in ROW.finditer((ROOT / page).read_text(encoding="utf-8")) for n in m.groups() if n}
    assert not tool_names - listed, f"{page} is missing: {sorted(tool_names - listed)}"
    assert not listed - tool_names, f"{page} lists tools the source does not define: {sorted(listed - tool_names)}"
