# Rocket League package snapshot — 2026-09-23, licensee 34

> Snapshot, not permanent truth. Re-check against the executable and
> `UStruct::SerializeExpr` before relying on it for another update.

## What changed

- Build `260918.75141.528314` (`++Prime+Update60-CL-528314`), installed
  2026-09-23. Epic and Steam report the same build.
- Package version/licensee `868/34`, generation count `11` (the 2026-09-03
  packages were `868/33`, generation count `10`).
- The cooked summary kept the licensee-33 layout: three zero `int32`s after
  `LastBlockSize`, twelve zero bytes after each `FCompressedChunkInfo`. The
  only change needed was widening `[Build(867, 868, 9u, 33u)]` to `34u`;
  without it the package was not recognised as Rocket League and failed with
  `EndOfStreamException`.

| Package | GUID | last-generation net objects |
|---|---|---:|
| Core | `21A590584A3AB63EB19C409AE52BBAB0` | 2785 |
| Engine | `B450275D4577FC20588BFB9C04EA4247` | 33364 |
| GFxUI | — | 824 |
| AkAudio | — | 635 |
| ProjectX | `2916AE654A8762C8100C4F863C742062` | 19662 |
| TAGame | `AAE66C754879D1661204A9B2BB4E191A` | 83974 |

## The opcode table did not rotate

`BuildGeneration7TokenMap` is still the right map. The evidence is the
bytecode rather than the dispatcher table, because no IDB of this build was
opened:

- 13 of 15 sampled TAGame functions are byte-identical between the
  2026-09-03 and 2026-09-23 packages (`RBActor_TA.PostBeginPlay`,
  `Car_TA.PostBeginPlay`, `Ball_TA.IsGroundHit`, `PRI_TA.SetLoadouts`,
  `PRI_TA.HandlePlayerNameChanged`, `Car_TA.UpdateTeamLoadout`,
  `Car_TA.GetPreviewTeamIndex`, `GFxShell_TA.ShowErrorMessage`,
  `Team_TA.SetLogo`, `PRI_TA.GetPing`, `Car_TA.FinishDemo`,
  `SaveData_TA.HandleDataLoaded`, `GFxHUD_TA.UpdateCarData`). A rotated
  permutation would change essentially every function.
- The two that differ (`Ball_TA.Explode`, `PRI_TA.ReplicateLoadoutToServer`)
  differ in content, not in encoding: the new `ReplicateLoadoutToServer` adds
  the stat-title RPCs and uses the same gen-7 bytes (`0F`, `2B`, `5E`, `19`,
  `1C`, `07`, `06`, `04 0B 4C`).
- The whole sentinel set in `CLAUDE.md` (21 functions across Engine and
  TAGame) decompiles with no warning, unresolved cast, truncation marker,
  ghost native or implausible literal.

If a later build *does* rotate, the procedure in
`GNATIVES_SNAPSHOT_v868.md` still applies: dump the dispatcher table from the
new executable and match handler addresses.

## New in the replicated PRI_TA stream

134 fields became 158. Among them `PlayerStatus`
(`_Types_TA.EPlayerStatusType`: `Unset`, `None`, `HonorDuelLaurel`, `_MAX`)
and two server RPCs between `ServerSetLoadoutDataProduct` and
`ServerSetLoadoutComplete`: `ServerAddOwnedStatTitle(OwnedStatTitleEntry)` and
`ServerSetEquippedTitleStat(int, ProductInstanceID)`. `OwnedStatTitleEntry` is
`{ ProductInstanceID InstanceID; int StatId; int StatValue; name TitleId; }`.
