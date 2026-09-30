# Safety

I6Pull is **read-only by construction**. It requests calibration parameters and writes a file on disk. It cannot change ECM calibration, because the code path that
would is not there and the paths that remain are gated.

## What is enforced, and where

| Rule | Enforced by |
|---|---|
| Transmit only J1939 transport (RTS `0x10`, CTS `0x11`, EOM-ACK `0x13` on `18EC00F9`; data on `18EB00F9`), 8-byte frames | `TxAllowed()` checked in `SendCan()`; anything else throws |
| Never transmit an 11-bit frame (VP44 fueling IDs `0x112`, `0x512`, `0x001`, `0x500` and all others) | `TxAllowed()` returns false for any non-29-bit frame |
| Only `ReadByNTN` (`0x48`); no `0x43`/`0x46` write opcodes, no download, program, erase or bootloader | `RequireReadRequest()` in `CanSendTpEf00()`, the single entry point for ECM requests |
| Never request password, key or boot-copy ITNs: `0005`, `0016`, `001E`-`0022`, `1000`, `1083`, `11AF`, `1267` | `BlockedItn` via `IsBlocked()` in the catalog loop and in `RequireReadRequest()` |
| ITNs come from the catalog only; `--itn` selects from it and cannot add to it; `--len` can shorten but not extend | `CatalogPull()` |
| Request length 1-1024 bytes (longer reads are chunked) | `RequireReadRequest()` |

`tests/SafetyGates.cs` checks the compiled `TxAllowed()` and `RequireReadRequest()` with allowed and refused inputs (36 checks). Adapter configuration uses three RP1210
commands only: `SET_MESSAGE_RECEIVE`, `ECHO_TX`, `ALL_FILTERS_PASS`.

## Operating rules

- Key-on, engine stopped. Do not use this as a running-engine fueling interface.
- Close INSITE and any other INLINE client first; the adapter is exclusive.
- Treat a dump as study data. It is a packed set of ITNs, **not** a flash image, not a recovery file, and not something to load back into an ECM.

## Known limits

- The hardened build compiles and its gates pass their tests, but it has not been re-run against a live ECM.
- A blocklist is only as good as its source. It was assembled from the ITN names in the catalog; ITNs outside the catalog are never requested at all.
- Reading is not risk-free on a 25-year-old ECM: a read while the bus is busy can time out. Stop and retry at key-on.

## Reporting

Found a way around a gate? Open an issue.
