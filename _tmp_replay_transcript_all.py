import json
import sys
from pathlib import Path

TRANSCRIPT = Path(r"C:\Users\bhsda\.cursor\projects\c-Users-bhsda-OneDrive-Desktop-Puck-Mod-Claude-Puck-Playground-DALF-MOD-LIBRARY-Dalf-Multisheet\agent-transcripts\963968ad-d337-44a0-9c2a-effbc7af9272\963968ad-d337-44a0-9c2a-effbc7af9272.jsonl")
REPO = Path(r"C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Claude Puck Playground\DALF MOD LIBRARY\Dalf Multisheet")

TARGETS = [
    "MultiSheetClientSettings.cs",
    "RinkScoreboardTab.cs",
    "RinkMotdShared.cs",
    "MinimapSessionOverride.cs",
    "TrainingObjectFactory.cs",
    "SlidableBoardCollision.cs",
    "SlidableGroundRaycastPatch.cs",
    "StickIcePassThrough.cs",
    "SlidableStickCollision.cs",
]

contents = {name: None for name in TARGETS}

def match_target(path: str):
    p = path.replace("\\", "/").lower()
    if "dalf multisheet" not in p:
        return None
    for name in TARGETS:
        if p.endswith(name.lower()):
            return name
    return None

with TRANSCRIPT.open("r", encoding="utf-8") as f:
    for line in f:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        for part in obj.get("message", {}).get("content", []):
            if part.get("type") != "tool_use":
                continue
            tool = part.get("name")
            inp = part.get("input", {})
            path = inp.get("path", "")
            key = match_target(path)
            if not key:
                continue
            if tool == "Write":
                contents[key] = inp.get("contents", "")
            elif tool == "StrReplace" and contents[key] is not None:
                old = inp.get("old_string")
                new = inp.get("new_string")
                if old is None or new is None:
                    continue
                if old not in contents[key]:
                    continue
                contents[key] = contents[key].replace(old, new, 1)

# default dest mapping
DEST = {
    "MultiSheetClientSettings.cs": REPO / "MultiSheetClientSettings.cs",
    "RinkScoreboardTab.cs": REPO / "RinkScoreboardTab.cs",
    "RinkMotdShared.cs": REPO / "RinkMotdShared.cs",
    "MinimapSessionOverride.cs": REPO / "MinimapSessionOverride.cs",
    "TrainingObjectFactory.cs": REPO / "_vendor/phltrainingcode-main/TrainingObjectFactory.cs",
    "SlidableBoardCollision.cs": REPO / "_vendor/phltrainingcode-main/SlidableBoardCollision.cs",
    "SlidableGroundRaycastPatch.cs": REPO / "_vendor/phltrainingcode-main/SlidableGroundRaycastPatch.cs",
    "StickIcePassThrough.cs": REPO / "_vendor/phltrainingcode-main/StickIcePassThrough.cs",
    "SlidableStickCollision.cs": REPO / "_vendor/phltrainingcode-main/SlidableStickCollision.cs",
}

ok = 0
for name, dest in DEST.items():
    content = contents.get(name)
    if not content:
        print(f"SKIP {name} (no transcript Write)")
        continue
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(content, encoding="utf-8", newline="\n")
    print(f"OK {name} ({len(content)} bytes)")
    ok += 1

print(f"Wrote {ok} files")
if ok == 0:
    sys.exit(1)
