import json
import sys
from pathlib import Path

TRANSCRIPT = Path(r"C:\Users\bhsda\.cursor\projects\c-Users-bhsda-OneDrive-Desktop-Puck-Mod-Claude-Puck-Playground-DALF-MOD-LIBRARY-Dalf-Multisheet\agent-transcripts\963968ad-d337-44a0-9c2a-effbc7af9272\963968ad-d337-44a0-9c2a-effbc7af9272.jsonl")
REPO = Path(r"C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Claude Puck Playground\DALF MOD LIBRARY\Dalf Multisheet")

TARGETS = {
    "StickIcePassThrough.cs": REPO / "_vendor/phltrainingcode-main/StickIcePassThrough.cs",
    "SlidableStickCollision.cs": REPO / "_vendor/phltrainingcode-main/SlidableStickCollision.cs",
}

def norm_path(p: str) -> str:
    return p.replace("\\", "/").lower()

contents = {name: None for name in TARGETS}
target_paths = {norm_path(str(v)): name for name, v in TARGETS.items()}

with TRANSCRIPT.open("r", encoding="utf-8") as f:
    for line in f:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        for part in obj.get("message", {}).get("content", []):
            if part.get("type") != "tool_use":
                continue
            name = part.get("name")
            inp = part.get("input", {})
            path = inp.get("path", "")
            key = target_paths.get(norm_path(path))
            if not key:
                continue
            if name == "Write":
                contents[key] = inp.get("contents", "")
            elif name == "StrReplace" and contents[key] is not None:
                old = inp.get("old_string")
                new = inp.get("new_string")
                if old is None or new is None:
                    continue
                cur = contents[key]
                if old not in cur:
                    print(f"WARN: StrReplace miss in {key}", file=sys.stderr)
                    continue
                contents[key] = cur.replace(old, new, 1)

for name, dest in TARGETS.items():
    content = contents.get(name)
    if not content:
        print(f"MISSING {name}", file=sys.stderr)
        sys.exit(1)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(content, encoding="utf-8", newline="\n")
    print(f"OK {name} ({len(content)} bytes)")
