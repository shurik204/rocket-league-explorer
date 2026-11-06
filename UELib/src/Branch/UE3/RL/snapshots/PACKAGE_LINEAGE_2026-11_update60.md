# Rocket League build lineage — Update 60 / 60.1 (Sept–Nov 2026)

> Snapshot, not permanent truth. Re-check against the executable and
> `UStruct::SerializeExpr` before relying on it for another update.

Four builds, in release order. The two middle ones were fetched from older
commits of [`shurik204/rocket-league-meta`](https://github.com/shurik204/rocket-league-meta)
(`data` branch) using `legendary --manifest`.

## How to fetch an older build

`legendary` accepts a local manifest, so you can pin any historic build:

```pwsh
# 1. Pick the commit that published the build you want.
#    https://github.com/shurik204/rocket-league-meta/commits/data
#    Each commit message is the build string, e.g.
#    Rocket League "++Prime+Update60.1-CL-530866"

# 2. Grab that commit's manifest (and Sugar.json for the build id).
curl -L -o Sugar.manifest `
  https://raw.githubusercontent.com/shurik204/rocket-league-meta/<sha>/Sugar.manifest

# 3. Download only the files you need.
legendary install Sugar `
  --manifest .\Sugar.manifest `
  --base-path . --game-folder .\game `
  --prefix "TAGame/CookedPCConsole/TAGame.upk" `
  --skip-sdl --force --no-install -y
```

`--prefix` is case-insensitive and matches path starts, so it can be repeated
per file (one invocation each). `--no-install` skips prerequisites/launch
setup. The downloaded UPKs are the **encrypted originals**; UELib's
`RocketLeaguePackageReader` opens them directly, no RLUPKTool step needed.

## Builds

| Changelist | meta commit | commit date | build date | `g_psyonix_build_id` | feature set | gens | TAGame exports |
|---|---|---|---|---:|---|---:|---:|
| `CL-528314` (`-pchotfix4`) | `9dd0e3ff2f` | 2026-09-26 | Sep 25 | `260918.75141.528314` | PrimeUpdate60 | 11 | 129,846 |
| `CL-529371` | `4357567dd7` | 2026-10-07 | Oct 1 | `261001.2867.529371` | PrimeUpdate60 | 12 | 129,850 |
| `CL-530866` | `57cf08a195` | 2026-10-21 | Oct 13 | `261013.63066.530866` | PrimeUpdate60_1 | 13 | 135,739 |
| `CL-533021` | `3df49f5529` | 2026-11-06 | Oct 31 | `261031.63733.533021` | PrimeUpdate60_1 | 14 | 136,575 |

Package version / licensee stay `868 / 34` and engine/cooker stay `10897 / 136`
across all four. Generation count increments by exactly one per release, so
`UnrealPackage.GameBuild`'s `[Build(867, 868, 9u, 34u)]` range covers all of
them. `RocketLeague.exe` file version stays `1.0.10897.0` — RL rotates the
changelist, not the engine version.

`CL-530866` is byte-identical to the `RL_60_1` fixture set (exe and all six
encrypted UPKs hash-match), so it is the same build under a different name.

## Build secret per build

`PsyNet_X.GetBuildSecret()` is a 64-byte XOR blob with a 4-byte rolling key.
Every release rotates both the blob location and the key, so the secret is
unique per build:

| Changelist | thunk | impl | blob | key | secret |
|---|---|---|---|---|---|
| `CL-528314` | `0x140DAE090` | `0x140DF6300` | `0x141D8BEE0` | `5720E216` | `ce31914a39dbf2f3ab13ea54d00b7fe87bc5473a2f04707d78de54039ded8951` |
| `CL-529371` | `0x140DAE090` | `0x140DF6300` | `0x141D8BEE0` | `FF2DFFC1` | `1c72102bca07fb3ba34ef640656901df0658107ffe17bb3a6e9cd1cc578392ea` |
| `CL-530866` | `0x140DB6D40` | `0x140DFE9F0` | `0x141DDCD00` | `11A0A47E` | `76656a5b51ec57a13549eef732c8a8367638aa3a7989bade1f7e4e5f93d57625` |
| `CL-533021` | `0x140DB6F10` | `0x140DFEF00` | `0x141DDD370` | `C52EF283` | `eba094a6fd9abbdf913dcbadf18ab59682671cdcc7cebc4902b81113273f5fa3` |

`CL-528314` and `CL-529371` share the same thunk/impl/blob address — only the
key changed, i.e. a pure data edit with no code change between those two.

Recompute any of them with `Tools/extract_build_secret.py <RocketLeague.exe>`.
The `CL-533021` value was independently confirmed against a live
`config.psynet.gg?buildSecret=…` request.

## Script deltas

### `CL-528314` → `CL-529371`

A hotfix. Exactly **one** function changes across all packages:

```
ProjectX.OnlineGameDedicatedServer_X.GetPlaylistTags
```

No classes added or removed.

### `CL-529371` → `CL-530866`  (Prime Update 60.1 content)

The real content drop: 13 classes added, 2 removed, 55 modified.

New in `TAGame`:

```
ProductVault_TA                        SkinForcedFinishCache_TA
GFxData_BundleShopPreview_TA           GFxData_DevMenu_TA
GFxData_OneTimeMainMenuMessage_TA      OneTimeMainMenuMessageQueue_TA
```

New in `ProjectX`: `FlatbufferMetrics_X`. New in `Core`: `UTF8` gains
`IsValidUTF8`/`Sanitize`; `Object` gains `SortAscendingQWord`/
`SortDescendingQWord`/`Subtract_QWordQWord`.

Also new here: `PsyNetUrl_X` native `execIsAllowedURL`/`execIsSecurityEnforced`,
`WebRequest_X` native `execStripUrlSecrets`, and
`OnlinePlayerAuthentication_X.EventOnMMRRefunded`. The
`PsyNetAuthKey`/`PsyEnvironmentSecret` headers already existed before this
range.

`Engine.TimeWindow.GetStartTime` changes return type `Qword` → `int`.

### `CL-530866` → `CL-533021`

Content-only. The only script change in the entire game is
`Core._Types_Generated`: `EContentKeyIndex_PrimeUpdate60_1` gains one key
inserted at index 6 (`…BBA91D89B0B561C8EBBAE35EAE776EBF`), shifting later
indices and taking `_MAX` from 22 to 23. All 2,691 `TAGame` classes, all
1,362 `Engine` classes and all 749 `ProjectX` classes decompile
byte-identically to `CL-530866`.

## Opcode map

`BuildGeneration7TokenMap` is correct for all four builds. Sentinel functions
(`Car_TA.PostBeginPlay`, `Ball_TA.IsGroundHit`,
`Car_TA.GetPreviewTeamIndex`, `PRI_TA.SetLoadouts`, `Ball_TA.Explode`,
`Engine.Pawn.PostBeginPlay`) decompile cleanly at every generation count
11 → 14, so the map must **not** be re-derived from the generation count.
`EngineBranchRL.BuildTokenMap` selects the gen-7 map for any package at
`Generations >= 7`, which is what these builds need.

Unresolved-marker counts (`/* unresolved */`, `/* truncated */`, `__NFUN_`):

| Build | markers |
|---|---:|
| `CL-528314` | 28 |
| `CL-529371` | 28 |
| `CL-530866` | 72 |
| `CL-533021` | 72 |

The jump between `CL-529371` and `CL-530866` tracks the newly added generated
anonymous classes, not a token-mapping regression.
