# I6Pull

Read-only dump of a **Dodge CM551** (ISB VP44, ~1998.5–2002) over a Cummins **INLINE 6** USB adapter. Pulls KennPar ITNs on raw CAN; it does not flash, program, erase, or talk to the VP44.

This is not Calterm and not INSITE. See [SAFETY.md](SAFETY.md).

## Hardware

- Cummins INLINE 6 USB (32-bit `CMNSI632.dll`)
- Dodge 3-pin Cummins datalink
- 250 kbps CAN, key-on (engine stopped is fine)

**Close INSITE first.** It holds the adapter exclusively.

## Build and run

```bat
build.bat
I6Pull.exe --out dump.jsonl
```

Then pack and decode (Python 3, no extra packages):

```bat
python python\pack_dump.py dump.jsonl dump.json
python python\decode_tune.py --meta catalog\chr0000_meta.json --a dump.json --html maps.html
```

Optional second ECM: pack another dump, then `--b dumpB.json` on `decode_tune.py`, or `python python\diff_ecm.py dump.json dumpB.json`.

## Companion dumps

Sample packed dumps live in [cm551-dodge-dumps](https://github.com/Bender1011001/cm551-dodge-dumps).

## Protocol

Facts for the KennPar read path: [docs/protocol.md](docs/protocol.md).
