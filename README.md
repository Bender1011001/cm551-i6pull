# I6Pull

[![safety gates](https://github.com/Bender1011001/cm551-i6pull/actions/workflows/ci.yml/badge.svg)](https://github.com/Bender1011001/cm551-i6pull/actions/workflows/ci.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A read-only diagnostic puller for the **Cummins CM551** engine controller in 1998.5-2002 Dodge Ram 5.9 Cummins trucks (ISB with Bosch VP44 pump), using a Cummins
INLINE 6 USB adapter over RP1210. It reads the ECM's calibration parameters ("ITNs") over the diagnostic CAN link and writes them to a JSONL file.

It is built so that it **cannot write**: the transmit path is an allowlist enforced in code and covered by tests, not just a promise in this README.
See [SAFETY.md](SAFETY.md).

## What it does

- Connects through the vendor's 32-bit `CMNSI632.dll` (you must have Cummins INLINE 6 drivers installed; nothing from Cummins is included here).
- Sends one kind of request: **ReadByNTN** (command `0x48`) over J1939 transport on 29-bit CAN, 250 kbps.
- Reads the 667 ITNs listed in [`catalog/chr0000_reads.csv`](catalog/chr0000_reads.csv), minus a blocklist of password, key and boot-copy ITNs.
- Writes one JSON line per ITN. Helper scripts pack, decode and diff dumps.

Protocol details: [docs/protocol.md](docs/protocol.md).

## Use

Key on, engine stopped. Close Cummins INSITE first (it holds the adapter exclusively).

```bat
build.bat                                 :: needs .NET Framework 4 csc.exe, produces I6Pull.exe (x86)
I6Pull.exe --out dump.jsonl
python python\pack_dump.py dump.jsonl dump.json
python python\decode_tune.py --meta catalog\chr0000_meta.json --a dump.json --html maps.html
python python\diff_ecm.py dump.json dumpB.json
```

| Option | Meaning |
|---|---|
| `--out FILE` | output JSONL (default `dump.jsonl`) |
| `--reads CSV` | catalog (default `catalog\chr0000_reads.csv`) |
| `--itn HEX` | read only this ITN; must already be in the catalog and not blocked. Repeatable |
| `--len N` | shorten a catalog read to N bytes (cannot extend it) |
| `--limit N` | stop after N ITNs |
| `--probe` | connect, print adapter info, disconnect |
| `--proto STR` | RP1210 connect string (default `CAN:Baud=250,Channel=1`) |
| `--verbose` | log every transmitted frame |

No binary is committed. Build it yourself so you know what you are running.

## Safety model, verified

The transmit surface is small enough to enumerate: J1939 transport frames (`RTS`, `CTS`, `EOM-ACK`, `DT`) from tool address `0xF9`, nothing else, and never an
11-bit frame (the ECM-to-pump fueling bus). Every request also passes a choke point that accepts `ReadByNTN` for a non-blocked ITN with a bounded length and refuses
everything else. `tests/SafetyGates.cs` compiles the real source and exercises both gates through reflection (36 checks, no hardware needed):

```powershell
.\tests\run_safety_gates.ps1
```

**Limits of that claim.** The gates are tested in isolation and the tool compiles; the hardened build has not been re-run against a live ECM since the checks were
added. The sample dumps were captured with the earlier build, which enforced a shorter blocklist only at catalog-read time.

## Companion projects

- [cm551-dodge-dumps](https://github.com/Bender1011001/cm551-dodge-dumps): two packed, VIN-redacted captures.
- [vp44tune](https://github.com/Bender1011001/vp44tune): calibration viewer/editor that embeds this puller.
- [dodge-24v-vp44-tuning](https://github.com/Bender1011001/dodge-24v-vp44-tuning): decoded factory maps and analysis.

MIT licensed. Use only on vehicles you own or are authorised to diagnose.
