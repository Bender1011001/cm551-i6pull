# CM551 INLINE 6 read path

Facts for the Dodge CM551 KennPar pull used by I6Pull. This tool only **reads**.

## Bus

Dodge 3-pin Cummins datalink is mixed CAN at **250 kbps**:

- 11-bit VP44 pump traffic
- 29-bit CPP / J1939-style traffic (this puller)

I6Pull never transmits 11-bit IDs `0x112`, `0x512`, `0x001`, or `0x500`.

## RP1210

- Vendor DLL: `CMNSI632.dll` (32-bit)
- Device id **254** (INLINE 6 USB)
- `CMNSI632.ini` has `RP1210=B`
- CAN format **4**
- `RP1210_ReadMessage(client, buffer, size, blocking)` — buffer, then size, then blocking
- Must send **SET_MESSAGE_RECEIVE** (command **18**) or RX stays empty

Typical connect string: `CAN:Baud=250,Channel=1`.

## Read request (command 0x48)

Tool address `0xF9`. Request CAN ID `0x18EF00F9`.

11-byte payload:

```
48 | NTN_be | offset_be | length_be
```

Sent with J1939 transport (BAM-style peer TP):

| Role | ID | Meaning |
|------|-----|---------|
| Tool RTS | `0x18EC00F9` | start of request |
| Tool DT | `0x18EB00F9` | request bytes |
| ECM RTS | `0x18ECF900` | start of reply |
| Tool CTS | `0x18EC00F9` | windows of **32** packets |
| ECM DT | `0x18EBF900` | reply bytes |

Reads longer than **1024** bytes are split into chunk requests.

## Reply (command 0x49)

Envelope:

```
49 | NTN_be | offset_be | length_be | data
```

I6Pull strips the 11-byte header and logs the data bytes.

## Catalog

`catalog/chr0000_reads.csv` lists ITN hex, byte length, and names. Password and boot-copy ITNs are omitted from the pull.
