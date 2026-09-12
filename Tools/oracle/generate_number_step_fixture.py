"""Generate the core fixture from the retained Chrome number-key capture."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[2]
rows = json.loads((root / "hosts/godot/project/number_step_cases.json").read_text(encoding="utf-8"))["rows"]
lines = ["// Frozen Chrome number-key expectations; regenerate with tools/oracle/generate_number_step_fixture.py."]
for row in rows:
    attrs = row["attrs"]
    html = '<input id=c type=number ' + ' '.join(
        key + '="' + value.replace('&', '&amp;').replace('"', '&quot;') + '"'
        for key, value in attrs.items() if key != "current") + '>'
    fields = [json.dumps(html), json.dumps(attrs.get("current", "")),
              str("current" in attrs).lower(),
              "WEVA_KEY_UP" if row["key"] == "ArrowUp" else "WEVA_KEY_DOWN",
              json.dumps(row["before"]), json.dumps(row["value"]),
              str(bool(row["events"])).lower()]
    lines.append("{" + ",".join(fields) + "},")
(root / "libweva/tests/number_step_cases.inc").write_text("\n".join(lines) + "\n", encoding="utf-8")
