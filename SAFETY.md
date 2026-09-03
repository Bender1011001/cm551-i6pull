# Safety

I6Pull is **upload/read only**. It requests KennPar ITNs and writes a JSONL dump on disk. It does not change ECM calibration.

## Never

- Download / Program / Flash / erase
- Jump to bootloader
- Scratch-pad or write commands
- Transmit VP44 11-bit fueling IDs `0x112`, `0x512`, `0x001`, `0x500` (listen only)
- Request password ITNs or the BOOTDST / boot-copy family

Blocked ITNs in this tree: `0005`, `0016`, `001E`–`0022`, `1083`, `11AF`, `1267`.

## Adapter

Close INSITE (and any other INLINE client) before running I6Pull. Key-on is enough; do not use this tool as a running-engine fueling interface.
