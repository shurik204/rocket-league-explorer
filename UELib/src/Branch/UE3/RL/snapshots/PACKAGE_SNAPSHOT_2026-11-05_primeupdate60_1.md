# Rocket League package snapshot — RL_60_1 (Prime Update 60.1)

> Snapshot, not permanent truth. Re-check against the executable and
> `UStruct::SerializeExpr` before relying on it for another update.

## Build and package identity

- Fixture folder: `RL_60_1`
- Executable: `RocketLeague.exe`, file version `1.0.10897.0`
- Package version / licensee: `868 / 34`
- Engine version / cooker version: `10897 / 136`
- Generation count: `13`
- Compression flags: `1` (zlib)
- Package group: `None`
- AES header encryption: unchanged Rocket League key (the one used by
  RLUPKTool / UModel).

This is a newer build than the 2026-09-23 `868/34` snapshot
(`PACKAGE_SNAPSHOT_2026-09-23_licensee34.md`, 11 generations). The opcode
table did **not** rotate again: `BuildGeneration7TokenMap` is still correct.
`TAGame.Car_TA.PostBeginPlay`, `TAGame.Ball_TA.IsGroundHit`,
`TAGame.Car_TA.GetPreviewTeamIndex`, `TAGame.PRI_TA.SetLoadouts`, and
`Engine.Pawn.PostBeginPlay` all decompile to the expected script with the
generation-7 map.

## What changed in the on-disk image

The summary layout is the same licensee-33/34 layout (three zero int32s after
`LastBlockSize`). The important difference is how the data after the header is
laid out:

- `DependsOffset` is the base of the uncompressed object-data image, not the
  start of a per-export dependency array.
- `TotalHeaderSize` points at the first **physical** `FCompressedChunkHeader`.
  The uncompressed image starts at `DependsOffset`, so the last
  `TotalHeaderSize - DependsOffset` bytes of the plaintext header area overlap
  the first bytes of decompressed chunk 0.
- Each physical chunk starts with `0x9E2A83C1`, a `BlockSize` (normally
  `0x20000`), compressed/uncompressed totals, then a block table and zlib
  blocks. There is no usable `FCompressedChunkInfo` array in the summary
  (`CompressedChunks.Count == 0`); walk the physical chunks instead.

For `Core.upk` the seven physical chunks inflate to 880,536 bytes and the full
image is 1,188,152 bytes (`DependsOffset` 307,616 + payload). For `TAGame.upk`
85 chunks inflate to 162,599,794 bytes and the full image is 174,285,757 bytes.

## Why RLUPKTool output showed `class X dllbind()`

RLUPKTool (the 2018 release) writes the decrypted header and only the first
decompressed chunk, into a file pre-sized to the **compressed** package length.
All export serial offsets beyond that point read past EOF. UELib catches the
incomplete object reads, leaves `UClass` fields at their defaults, and the
decompiler ends up rendering `class X dllbind()` (an empty/misread
`DLLBindName`).

The 2018 tool also cannot be used as-is here because its summary reader expects
a non-empty `CompressedChunks` array; recent RL packages keep that array empty
and store the chunk stream after `TotalHeaderSize`.

## Fix in UELib

`RocketLeaguePackageReader` (under `UELib/src/Branch/UE3/RL/`) now:

1. Detects encrypted/truncated RL packages from the plaintext summary.
2. Decrypts the header region with the RL AES-256-ECB key.
3. Walks the physical chunk stream at `TotalHeaderSize`, inflates every zlib
   block, and rebuilds the logical image at `DependsOffset`.
4. Writes the rebuilt image to a temp cache and lets `UnrealLoader` open it
   while keeping the original package name/path.
5. Falls back to a sibling `<name>_.upk` when a truncated decrypted file (the
   kind produced by RLUPKTool) is opened.

Opening either the encrypted original or the truncated decrypted file now
produces the same decompiled classes and functions.
