#!/usr/bin/env python3
"""Compare two packed ECM dump JSON files by ITN hex."""
from __future__ import annotations

import json
import sys
from pathlib import Path

BLOCKED = {"0005", "0016", "001E", "001F", "0020", "0021", "0022", "1083", "11AF", "1267"}


def load(path: Path) -> dict:
    if path.suffix.lower() == ".jsonl":
        packed = {}
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            if not line.strip():
                continue
            o = json.loads(line)
            if o.get("k") != "ntn":
                continue
            m = o.get("m", "")
            if "no payload" in m or not m.startswith("ITN "):
                continue
            itn = m.split()[1].upper()
            if itn in BLOCKED or " data=" not in m:
                continue
            hx = m.split(" data=", 1)[1]
            parts = hx.split(" ", 1)
            packed[itn] = parts[1] if len(parts) > 1 else ""
        return packed
    obj = json.loads(path.read_text(encoding="utf-8"))
    first = next(iter(obj.values()), None)
    if isinstance(first, dict) and "hex" in first:
        return {k.upper(): v.get("hex", "") for k, v in obj.items() if k.upper() not in BLOCKED}
    return {str(k).upper(): v for k, v in obj.items() if str(k).upper() not in BLOCKED}


def main():
    if len(sys.argv) < 3:
        print("usage: diff_ecm.py dump_a.json dump_b.json")
        return 2
    a = load(Path(sys.argv[1]))
    b = load(Path(sys.argv[2]))
    keys = sorted(set(a) | set(b))
    n = 0
    for k in keys:
        va = a.get(k, "<missing>")
        vb = b.get(k, "<missing>")
        if va != vb:
            n += 1
            print("%s\n  A: %s\n  B: %s" % (k, va, vb))
    print("differences", n, "of", len(keys), "ITNs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
