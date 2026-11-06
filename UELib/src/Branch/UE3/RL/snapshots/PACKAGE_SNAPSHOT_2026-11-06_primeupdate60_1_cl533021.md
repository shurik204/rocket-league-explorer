# Rocket League package snapshot — 2026-11-06, Prime Update 60.1 CL-533021

> Snapshot, not permanent truth. Re-check against the executable and
> `UStruct::SerializeExpr` before relying on it for another update.

## Build identity

- Build: `++Prime+Update60.1-CL-533021`
- `g_psyonix_build_id`: `261031.63733.533021`
- Build date (from the manifest metadata): `Oct 31 2026 18:08:23`
- Feature set: `PrimeUpdate60_1`
- `RocketLeague.exe` file version: `1.0.10897.0` (unchanged — RL keeps the
  engine version stable and rotates the changelist instead)
- Package version / licensee: `868 / 34` (unchanged)
- Engine / cooker version: `10897 / 136` (unchanged)

Everything below was read from a copy fetched with `legendary` using
`--prefix`, so only the listed files were downloaded.

## The opcode table did not rotate

`BuildGeneration7TokenMap` is still correct. All 2691 script classes in
`TAGame` decompile to **byte-identical** text compared to the previous build,
and the sentinel set (`Car_TA.PostBeginPlay`, `Ball_TA.IsGroundHit`,
`Car_TA.GetPreviewTeamIndex`, `PRI_TA.SetLoadouts`, `Ball_TA.Explode`,
`Engine.Pawn.PostBeginPlay`) decompiles cleanly. An opcode rotation would
change essentially every function.

The unresolved-token counts are also unchanged from the previous build
(`/* unresolved */` 61, `/* truncated */` 11, `__NFUN_` 0), so nothing new
regressed.

## Package deltas

`Core` and `TAGame` are the only script packages that actually changed.
`Engine`, `ProjectX`, `IpDrv` and `WinDrv` decompile identically.

| Package | Summary | Previous | This build |
|---|---|---|---|
| TAGame | size | 81,930,613 | 82,210,250 |
| TAGame | names | 54,712 | 54,714 |
| TAGame | exports | 135,739 | 136,575 |
| TAGame | generations | 13 | 14 |
| TAGame | `DependsOffset` | 11,685,963 | 11,746,239 |
| Core | names | 2,707 | 2,708 |
| Core | generations | 13 | 14 |
| Core | `DependsOffset` | 307,616 | 307,689 |

Both packages append one new generation. The extra exports in `TAGame` are
almost entirely zero-sized `None` placeholders (35,097 → 35,931); the real
class count is unchanged at 2691. `Product_TA` exports go 9502 → 9504.

### New names in TAGame

```
+ p1ee7e930fac01f2ecdd9      (new product id)
+ p2527cd142d029cfa918d      (new product id)
- Spikerush                  (renamed)
+ SpikeRush
```

### Content key rotation

`Core._Types_Generated.EContentKeyIndex_PrimeUpdate60_1` gains one entry,
**inserted at index 6**, so every subsequent index shifts by one and
`_MAX` goes 22 → 23:

```
PrimeUpdate60_1_BBA91D89B0B561C8EBBAE35EAE776EBF   // 6  (new)
```

This is the only script-level change in the whole build. Older
`EContentKeyIndex_*` enums are untouched.

## PsyNet build secret

`PsyNet_X.GetBuildSecret()` is still `native static final` and still an XOR
decode of a 64-byte blob plus a 4-byte repeating key stored immediately after
it. Only the data moved:

| Item | Previous build | This build (CL-533021) |
|---|---|---|
| `execGetBuildSecret` thunk | `0x140DB6D40` | `0x140DB6F10` |
| Implementation | `0x140DFE9F0` | `0x140DFEF00` |
| Cipher blob | `0x141DDCD00` | `0x141DDD370` |
| Key | `11 A0 A4 7E` | `C5 2E F2 83` |
| Secret | `76656a5b51ec57a13549eef732c8a8367638aa3a7989bade1f7e4e5f93d57625` | `eba094a6fd9abbdf913dcbadf18ab59682671cdcc7cebc4902b81113273f5fa3` |

`Tools/extract_build_secret.py` recomputes this from any `RocketLeague.exe`
without hard-coded addresses: it finds the `UPsyNet_XexecGetBuildSecret`
registration entry, follows the thunk's first direct `call`, then reads the
`lea r8,[blob]` / `lea r9,[blob_end]` pair. The result for this build was
independently confirmed against a live `config.psynet.gg` request
(`?buildSecret=…`).

The secret is not derived from the changelist (MD5/SHA-1/SHA-256 of `533021`,
`261031.63733.533021`, etc. do not match) and never appears as plaintext in
the executable.

## PsyNet headers are unchanged

`InitHeaders` still sets `PsyBuildID`, `PsyFeatureSet`, `PsyEnvironment`,
`PsyNetAuthKey` and `PsyEnvironmentSecret`. The last two are still gated on
non-empty `PsyNetKeys` values, which stay empty in a normal build, so they
never appear in captured traffic.
