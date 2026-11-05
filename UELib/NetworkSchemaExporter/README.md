# UE3 network schema exporter

This read-only command-line tool exports the version-sensitive package map and
class net-field ordering required by an external UE3 replication decoder. Its
first consumer is NebulaProxy's Rocket League ball/car telemetry and passive
client-loadout RPC observation paths.

Rocket League packages must be decrypted with RLUPKTool before they are passed
to this exporter.

```powershell
dotnet run --project .\UELib\NetworkSchemaExporter -- `
  .\rl-network-schema.json `
  C:\path\to\Core_decrypted.upk `
  C:\path\to\Engine_decrypted.upk `
  C:\path\to\IpDrv_decrypted.upk `
  C:\path\to\ProjectX_decrypted.upk `
  C:\path\to\TAGame_decrypted.upk `
  C:\path\to\ARC_Standard_P_decrypted.upk `
  C:\path\to\Stadium_P_decrypted.upk `
  C:\path\to\GameInfo_Soccar_SF_decrypted.upk
```

The generated JSON records package versions, GUIDs, per-generation net-object
counts, trajectory/radar/boost actor objects, inheritance, ordered local
network fields (including `PRI_TA` loadout RPC metadata), and resolved boost
archetype positions/defaults. Referenced
arena prefab packages must be supplied when map actors inherit their transforms
from those prefabs. Each net object also records its runtime
`wire_package_name`, which can differ from the source `.upk` for forced exports
inside seek-free packages. It never writes to an input package.

If the output file already contains the separately measured
`wire_package_prefix` or `wire_package_profiles` arrays, regeneration preserves
them. Package deserialization alone cannot infer that live connection ordering.
