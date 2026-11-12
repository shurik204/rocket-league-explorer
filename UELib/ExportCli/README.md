# uelib-export

A small command-line harness around Eliot.UELib's UnrealScript decompiler. Point
it at a package and get one `.uc` file per class — no GUI, no manual
decryption step for Rocket League packages.

```pwsh
uelib-export TAGame.upk
```

Writes `./TAGame_decompiled/<ClassName>.uc` and prints each written path on
stdout (progress and warnings go to stderr):

```pwsh
uelib-export TAGame.upk | xargs wc -l
```

## Building a single binary

Prebuilt binaries for `win-x64`, `linux-x64` and `osx-arm64` are attached to the
GitHub releases (`uelib-export-<platform>`), built by CI on every version tag.

```pwsh
# Linux
dotnet publish UELib/ExportCli/Eliot.UELib.ExportCli.csproj -c Release -r linux-x64

# Windows
dotnet publish UELib/ExportCli/Eliot.UELib.ExportCli.csproj -c Release -r win-x64

# macOS (Apple silicon)
dotnet publish UELib/ExportCli/Eliot.UELib.ExportCli.csproj -c Release -r osx-arm64
```

The output is `UELib/ExportCli/bin/Release/net9.0/<rid>/publish/uelib-export` (`.exe` on
Windows) — a single self-contained file with the .NET runtime and every
dependency bundled in. The `RuntimeIdentifier`-conditioned properties in
`Eliot.UELib.ExportCli.csproj` enable `PublishSingleFile` + `SelfContained`
automatically, so no extra publish flags are needed.

`dotnet build` (framework-dependent, no RID) still works for quick iteration;
run it with `dotnet UELib/ExportCli/bin/Debug/net9.0/uelib-export.dll <package>`.

> **Linux/macOS and strong-name signing.** `Eliot.UELib` is signed with a SHA-1
> strong-name key, which OpenSSL 3 (default on modern Linux distros) rejects
> with `digital envelope routines::invalid digest`. The ExportCli project passes
> `SignAssembly=false` to the library reference on non-Windows platforms,
> which only affects the local build output.

> **Not the same as `UELib/CLI`.** The older `Eliot.UELib.CLI` project next door
> is an interactive object inspector (`obj list/get/decompile`). This exporter
> is non-interactive and writes whole classes to disk.

## Usage

```
uelib-export <package> [options]

  -o, --out <dir>        Output directory (default: ./<PackageName>_decompiled)
  -b, --build <name>     Force the build target, e.g. RocketLeague, UDK, UT3, UT2004
  -n, --ntl <file>       Native table list (.NTL) used to name native functions
      --filter <text>    Only export classes whose name contains <text>
      --tree             Mirror group paths into subdirectories instead of one folder
      --clean            Delete existing .uc files in the output directory first
      --encoding <name>  utf8 | utf8-bom (default) | ansi
  -q, --quiet            Suppress progress output (errors are still reported)
  -h, --help             Show this help
```

Exit codes: `0` success, `1` fatal error, `2` usage error, `3` some classes
failed to decompile (the rest were written).

### Examples

```pwsh
# One package, default output folder
uelib-export /path/to/TAGame.upk

# Only Car_TA-like classes, ANSI output for tools that expect Windows-1252
uelib-export TAGame.upk -o ./scripts --filter Car_TA --encoding ansi

# Re-export in place, removing stale .uc files first, quiet
uelib-export Engine.upk -o ./engine --clean -q

# Use a native table list so natives render as names/operators
uelib-export Engine.upk -n "UE Explorer/Native Tables/NativesTableList_RocketLeague.NTL"
```

## Notes

- Rocket League packages are decrypted/rebuilt in memory by
  `RocketLeaguePackageReader`, so encrypted originals and truncated RLUPKTool
  output both load directly (see `UELib/src/Branch/UE3/RL/`).
- Class build targets are auto-detected from the package version; `--build` only
  needs to be set when detection picks the wrong branch.
- `--ntl` is optional. Without it, native names come from the package's own
  `UFunction` natives plus (for Rocket League) the binary-extracted GNatives
  fallback table.
- Output source is produced by `UClass.Decompile()` — the same code path the
  GUI uses for "Export scripts", including the RL-specific token mappings.
