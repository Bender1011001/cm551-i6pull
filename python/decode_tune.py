#!/usr/bin/env python3
"""Decode Chr0000 KennPars from packed I6Pull dumps into HTML (A, optional B)."""
from __future__ import annotations

import argparse
import html
import json
from collections import defaultdict
from pathlib import Path

BLOCKED = {
    "BOOTDST", "BOOTKEY", "BOOTLEN", "BOOTSRC",
    "ADJPSWD1", "ADJPSWD2", "ADJPSWD3", "ADJPSWD4", "ADJPSWD5", "ADJPSWD6",
    "ECM_PSWD", "OEM_PSWD", "DPFLPSWD",
}
BLOCKED_ITN = {"0005", "0016", "001E", "001F", "0020", "0021", "0022", "1083", "11AF", "1267"}


def norm_itn(s) -> str:
    s = str(s or "").strip().upper()
    if s.startswith("0X"):
        s = s[2:]
    if s and all(c in "0123456789ABCDEF" for c in s) and len(s) <= 4:
        return s.zfill(4)
    return s


def load_meta(path: Path) -> list:
    rows = json.loads(path.read_text(encoding="utf-8"))
    out = []
    for rec in rows:
        r = dict(rec)
        r["itn"] = norm_itn(r.get("itn"))
        z = r.get("z_itn") or ""
        r["z_itn"] = norm_itn(z) if z not in ("", "0") else ""
        r["axis"] = (r.get("axis") or "").strip()
        r["name"] = r.get("name") or ""
        r["units"] = r.get("units") or ""
        r["comment"] = r.get("comment") or ""
        r["size"] = int(r.get("size") or 2)
        r["offset"] = int(r.get("offset") or 0)
        r["signed"] = bool(r.get("signed"))
        try:
            r["scale"] = float(r.get("scale") if r.get("scale") not in (None, "") else 1.0)
        except (TypeError, ValueError):
            r["scale"] = 1.0
        try:
            r["add"] = float(r.get("add") if r.get("add") not in (None, "") else 0.0)
        except (TypeError, ValueError):
            r["add"] = 0.0
        if r["itn"] in BLOCKED_ITN or r["name"] in BLOCKED:
            continue
        out.append(r)
    return out


def load_dump(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def blob(dump, itn: str):
    if not dump or not itn:
        return None
    rec = dump.get(itn) or dump.get(itn.lstrip("0") or "0")
    if not rec:
        key = itn.lower()
        rec = dump.get(key)
    if not rec:
        return None
    hx = rec.get("hex") or ""
    if hx.startswith("0D 08 48"):
        return None
    if not hx.strip():
        return b""
    return bytes(int(x, 16) for x in hx.split())


def u_at(b: bytes, off: int, size: int, signed: bool):
    chunk = b[off:off + size]
    if len(chunk) < size or size not in (1, 2, 4):
        return None
    return int.from_bytes(chunk, "big", signed=signed)


def phys(raw, scale, add):
    return raw * scale + add


def fmt(v):
    if v is None:
        return "—"
    if isinstance(v, str):
        return v
    if abs(v - round(v)) < 1e-6:
        return str(int(round(v)))
    if abs(v) >= 100:
        return "%.1f" % v
    if abs(v) >= 1:
        return "%.3f" % v
    return "%.6g" % v


def decode_axis(b: bytes, rec):
    size = rec["size"] or 2
    if not b or len(b) < 2:
        return []
    nbytes = int.from_bytes(b[0:2], "big")
    if nbytes < size or nbytes > 400 or nbytes % size:
        n = min(32, len(b) // size)
        pts = []
        for i in range(n):
            raw = u_at(b, i * size, size, rec["signed"])
            if raw is None:
                break
            pts.append(phys(raw, rec["scale"], rec["add"]))
        return pts
    n = nbytes // size
    pts = []
    for i in range(n):
        raw = u_at(b, 2 + i * size, size, rec["signed"])
        if raw is None:
            break
        pts.append(phys(raw, rec["scale"], rec["add"]))
    return pts


def decode_scalar(b: bytes, rec):
    units = (rec["units"] or "").upper()
    if units in ("ASCI", "ASCII") or rec["name"] in ("DATADATE", "CAL_DATE", "ROM_DATE", "ECM_PN"):
        chunk = b[rec["offset"]:rec["offset"] + (rec["size"] or 2)]
        return chunk.decode("latin-1", "replace").rstrip("\x00 ")
    raw = u_at(b, rec["offset"], rec["size"] or 2, rec["signed"])
    if raw is None:
        return None
    return phys(raw, rec["scale"], rec["add"])


def decode_z(b: bytes, rec, nx, ny):
    size = rec["size"] or 2
    need = nx * ny * size
    if not b or nx <= 0 or ny <= 0 or len(b) < need:
        return None
    grid = []
    o = rec["offset"]
    for y in range(ny):
        row = []
        for x in range(nx):
            raw = u_at(b, o + (y * nx + x) * size, size, rec["signed"])
            row.append(None if raw is None else phys(raw, rec["scale"], rec["add"]))
        grid.append(row)
    return grid


def cell(a, b, has_b: bool):
    sa = fmt(a)
    if not has_b:
        return "<td class=same>%s</td>" % html.escape(sa)
    sb = fmt(b)
    if a is None and b is None:
        return "<td>—</td>"
    if sa == sb:
        return "<td class=same>%s</td>" % html.escape(sa)
    return "<td class=diff>%s<br><span class=meta>B %s</span></td>" % (
        html.escape(sa), html.escape(sb))


def main():
    ap = argparse.ArgumentParser(description="Decode packed CM551 dumps using catalog meta JSON (no E2M required).")
    ap.add_argument("--meta", required=True, help="catalog/chr0000_meta.json")
    ap.add_argument("--a", required=True, help="packed dump JSON from pack_dump.py")
    ap.add_argument("--b", help="optional second packed dump JSON")
    ap.add_argument("--html", default="maps.html")
    ap.add_argument("--json", dest="json_out")
    args = ap.parse_args()

    rows = load_meta(Path(args.meta))
    A = load_dump(Path(args.a))
    B = load_dump(Path(args.b)) if args.b else {}
    has_b = bool(args.b)

    z_axes = defaultdict(lambda: {"X": None, "Y": None})
    for r in rows:
        if r["axis"] == "G":
            continue
        if r["axis"] in ("X", "Y") and r["z_itn"]:
            z_axes[r["z_itn"]][r["axis"]] = r

    maps = []
    scalars = []
    seen_z = set()
    for r in rows:
        if r["axis"] == "G":
            continue
        if r["axis"] == "Z" and r["itn"] not in seen_z:
            seen_z.add(r["itn"])
            xr, yr = z_axes[r["itn"]]["X"], z_axes[r["itn"]]["Y"]
            xa = decode_axis(blob(A, xr["itn"]), xr) if xr and blob(A, xr["itn"]) else []
            xb = decode_axis(blob(B, xr["itn"]), xr) if has_b and xr and blob(B, xr["itn"]) else []
            ya = decode_axis(blob(A, yr["itn"]), yr) if yr and blob(A, yr["itn"]) else []
            yb = decode_axis(blob(B, yr["itn"]), yr) if has_b and yr and blob(B, yr["itn"]) else []
            xpts = xa or xb
            ypts = ya or yb
            ga = decode_z(blob(A, r["itn"]), r, len(xpts), len(ypts)) if blob(A, r["itn"]) else None
            gb = decode_z(blob(B, r["itn"]), r, len(xpts), len(ypts)) if has_b and blob(B, r["itn"]) else None
            maps.append({
                "z": r, "x": xr, "y": yr,
                "xpts_a": xa, "xpts_b": xb, "ypts_a": ya, "ypts_b": yb,
                "grid_a": ga, "grid_b": gb,
            })
        elif r["axis"] in ("N", "C", "B", "T", "A", "S") and r["size"] in (1, 2, 4) and r["offset"] + r["size"] <= 4096:
            ba, bb = blob(A, r["itn"]), blob(B, r["itn"]) if has_b else None
            va = decode_scalar(ba, r) if ba else None
            vb = decode_scalar(bb, r) if bb else None
            scalars.append({"rec": r, "a": va, "b": vb})

    title = "CM551 A vs B" if has_b else "CM551 dump"
    nav = title + ' — <a href="#maps">maps</a> · <a href="#scalars">scalars</a>'
    parts = ["""<!DOCTYPE html><html><head><meta charset="utf-8">
<title>%s</title>
<style>
body{font:14px/1.4 system-ui,Segoe UI,sans-serif;margin:24px;background:#111;color:#eee}
h1,h2,h3{color:#fff}
a{color:#8cf}
table{border-collapse:collapse;margin:8px 0 24px;font-size:12px}
th,td{border:1px solid #444;padding:3px 6px;text-align:right;white-space:nowrap}
th{background:#222;position:sticky;top:0}
td.diff,th.diff{background:#5a3a00;color:#ffe08a}
td.same{color:#ccc}
.meta{color:#aaa}
.name{text-align:left}
.nav{position:sticky;top:0;background:#111;padding:8px 0;z-index:2;border-bottom:1px solid #333}
</style></head><body>
<div class="nav">%s</div>
<h1>Tune parameters</h1>
<p class="meta">Decoded from catalog meta over live ReadByNTN dumps. Passwords and boot-copy ITNs omitted.
Grid is Y (rows) &times; X (columns).%s</p>
<h2 id="maps">Maps (%d)</h2>
""" % (html.escape(title), nav,
       " Yellow = A and B differ." if has_b else "",
       len(maps))]

    json_maps = []
    for m in maps:
        z = m["z"]
        x, y = m["x"], m["y"]
        xpts = m["xpts_a"] or m["xpts_b"]
        ypts = m["ypts_a"] or m["ypts_b"]
        ga, gb = m["grid_a"], m["grid_b"]
        title_m = "%s (%s)" % (z["name"], z["units"])
        cmt = html.escape(z["comment"][:180])
        xn = x["name"] if x else "?"
        yn = y["name"] if y else "?"
        parts.append("<h3 id='%s'>%s</h3><p class=meta>%s<br>X %s &middot; Y %s &middot; %d &times; %d</p>" % (
            html.escape(z["name"]), html.escape(title_m), cmt, html.escape(xn), html.escape(yn),
            len(ypts), len(xpts)))
        if not ga and not gb:
            parts.append("<p>no data</p>")
            continue
        parts.append("<table><tr><th></th>")
        for xv in xpts:
            parts.append("<th>%s</th>" % html.escape(fmt(xv)))
        parts.append("</tr>")
        ny = len(ypts)
        nx = len(xpts)
        for yi in range(ny):
            parts.append("<tr><th>%s</th>" % html.escape(fmt(ypts[yi])))
            for xi in range(nx):
                av = ga[yi][xi] if ga else None
                bv = gb[yi][xi] if gb else None
                parts.append(cell(av, bv, has_b))
            parts.append("</tr>")
        parts.append("</table>")
        json_maps.append({
            "name": z["name"], "units": z["units"], "comment": z["comment"],
            "x_name": xn, "y_name": yn, "x": xpts, "y": ypts,
            "a": ga, "b": gb if has_b else None,
        })

    skip_pfx = ("DG", "TI", "OC", "EP", "JC", "CD", "SS", "DR", "DC", "SI", "BLOK", "PREFAULT")
    if has_b:
        parts.append("<h2 id='scalars'>Scalar cal items</h2><table><tr>"
                     "<th class=name>name</th><th>A</th><th>B</th><th>units</th><th class=name>comment</th></tr>")
    else:
        parts.append("<h2 id='scalars'>Scalar cal items</h2><table><tr>"
                     "<th class=name>name</th><th>value</th><th>units</th><th class=name>comment</th></tr>")
    json_sc = []
    nsc = 0
    for s in scalars:
        r = s["rec"]
        if r["name"].startswith(skip_pfx):
            continue
        if r["subfile"] == "0" and r["axis"] != "B":
            continue
        nsc += 1
        if has_b:
            cls = "diff" if fmt(s["a"]) != fmt(s["b"]) else "same"
            parts.append(
                "<tr class='%s'><td class=name>%s</td><td>%s</td><td>%s</td><td>%s</td><td class=name>%s</td></tr>" % (
                    cls, html.escape(r["name"]), html.escape(fmt(s["a"])), html.escape(fmt(s["b"])),
                    html.escape(r["units"]), html.escape(r["comment"][:120])))
        else:
            parts.append(
                "<tr class='same'><td class=name>%s</td><td>%s</td><td>%s</td><td class=name>%s</td></tr>" % (
                    html.escape(r["name"]), html.escape(fmt(s["a"])),
                    html.escape(r["units"]), html.escape(r["comment"][:120])))
        json_sc.append({
            "name": r["name"], "units": r["units"], "a": s["a"],
            "b": s["b"] if has_b else None, "comment": r["comment"],
            "diff": has_b and fmt(s["a"]) != fmt(s["b"]),
        })
    parts.append("</table><p class=meta>%d scalars (diag/trip/J1939/snapshot omitted)</p></body></html>" % nsc)

    html_path = Path(args.html)
    html_path.write_text("".join(parts), encoding="utf-8")
    print("maps", len(maps), "scalars_in_html", nsc, "wrote", html_path)
    if args.json_out:
        json_path = Path(args.json_out)
        json_path.write_text(
            json.dumps({"maps": json_maps, "scalars": json_sc}, indent=2, default=str),
            encoding="utf-8")
        print("wrote", json_path)


if __name__ == "__main__":
    main()
