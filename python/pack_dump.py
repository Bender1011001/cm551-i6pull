#!/usr/bin/env python3
"""Pack I6Pull catalog JSONL into a compact ITN -> bytes JSON for diffs."""
from __future__ import annotations

import json
import sys
from pathlib import Path

BLOCKED = {"0005", "0016", "001E", "001F", "0020", "0021", "0022", "1083", "11AF", "1267"}


def pack(path: Path) -> dict:
    out = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        o = json.loads(line)
        if o.get("k") != "ntn":
            continue
        m = o["m"]
        if "no payload" in m:
            continue
        itn = m.split()[1]
        if itn in BLOCKED:
            continue
        name = ""
        if "name=" in m:
            name = m.split("name=", 1)[1].split(" req=", 1)[0]
        hx = m.split(" data=", 1)[1]
        parts = hx.split(" ", 1)
        nlen = int(parts[0])
        hexpart = parts[1] if len(parts) > 1 else ""
        rec = {"name": name, "len": nlen, "hex": hexpart}
        b = bytes(int(x, 16) for x in hexpart.split()) if hexpart.strip() else b""
        ascii_s = "".join(chr(x) if 32 <= x < 127 else "." for x in b).strip(" .")
        if len(ascii_s) >= 3:
            rec["ascii"] = ascii_s[:120]
        out[itn] = rec
    return out


def main():
    if len(sys.argv) < 2:
        print("usage: pack_dump.py dump.jsonl [dump.json]")
        return 2
    src = Path(sys.argv[1])
    dst = Path(sys.argv[2]) if len(sys.argv) > 2 else src.with_suffix(".json")
    data = pack(src)
    dst.write_text(json.dumps(data, indent=2), encoding="utf-8")
    print("itns", len(data), "wrote", dst)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
