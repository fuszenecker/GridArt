# AGENTS.md

Guidance for AI agents working in this repository.

## What this project is

GridArt is a .NET worker that builds **photomosaics**. Given a base image and a folder of
additional images, it produces a new image that:

- **reproduces the base image when viewed zoomed out** (or downscaled), and
- **resolves into the individual source images when zoomed in**.

The output keeps the base image's aspect ratio. "Looks like the base image" is not a vibe — it is
measured as a mean CIE76 ΔE between corresponding cells of the mosaic and the base image, reported at
the end of every run.

## Layout

| Path | Purpose |
| --- | --- |
| `GridArt.sln` | Solution; both projects are attached to it |
| `src/gridart.csproj` | The worker (`Microsoft.NET.Sdk.Worker`, net10.0) |
| `src/Program.cs` | Host wiring, config binding, usage guard |
| `src/CommandLine.cs` | Positional args, short/long aliases, unknown-switch check, usage text |
| `src/MosaicOptions.cs` | Every tunable, bound from the `Mosaic` config section |
| `src/Worker.cs` | Runs one job, saves the output, sets the exit code, stops the host |
| `src/Imaging/MosaicBuilder.cs` | Grid sizing, cell matching, rendering, colour adjust, quality scoring |
| `src/Imaging/TileLibrary.cs` | Parallel tile loading, resize + centre-crop, fingerprinting |
| `src/Imaging/TileCache.cs` | On-disk cache of decoded, cell-sized tile pixels |
| `src/Imaging/ColorSignature.cs` | Per-region perceptual fingerprint used for matching |
| `src/Imaging/ColorMath.cs` | sRGB ↔ linear ↔ CIELAB conversions, ΔE |
| `tests/gridart.Tests/` | xUnit suite over generated fixtures |

## Usage

```
gridart <base-image> <tiles-folder> [options]
```

Argument 1 is the base image, argument 2 is the folder of additional images. Both are positional
and required.

```bash
dotnet run --project src -- ./base.jpg ./photos -n 80 -s 48 -f
```

Output defaults to `<base-image-name>.mosaic.png` beside the base image. Exit code is 0 on success,
1 on failure. `--help` prints usage and exits 0; missing arguments or an unknown option print the
specific problem plus usage to stderr and exit 1.

Paths may be Windows (`C:\photos\base.png`), forward-slash (`C:/photos/base.png`), or
separator-leading (`/c/photos/base.png`) — all are accepted. The tiles folder is scanned recursively
by default, so images in subfolders are included.

### Options

| Short | Long | Config key | Default | Effect |
| --- | --- | --- | --- | --- |
| `-o` | `--output` | `OutputPath` | `<base>.mosaic.png` | Where the mosaic is written |
| `-n` | `--tiles-across` | `TilesAcross` | 64 | Tiles along the **longer** axis; the short axis follows the aspect ratio |
| `-s` | `--tile-size` | `TileSize` | 48 | Pixels per tile — how legible tiles are when zoomed in |
| `-g` | `--signature-grid` | `SignatureGrid` | 3 | Match patches per axis; 1 = average colour only, >1 also matches structure |
| `-c` | `--color-adjust` | `ColorAdjustStrength` | 0.35 | 0 = untouched tiles, 1 = exactly the base image |
| `-r` | `--max-reuse` | `MaxTileReuse` | 0 (unlimited) | Cap on placements per source image |
| `-d` | `--repeat-distance` | `RepeatAvoidanceRadius` | 2 | Cells within which a tile is not repeated |
| | `--recursive` | `Recursive` | true | Scan subfolders of the tiles folder |
| `-f` | `--overwrite` | `Overwrite` | false | Replace an existing output file |
| | `--no-cache` | `NoCache` | false | Skip the decoded-tile cache for this run |
| | `--cache-dir` | `CacheDirectory` | `%LOCALAPPDATA%\GridArt\cache` | Cache location |
| | `--clear-cache` | `ClearCache` | false | Delete all cache files before running |

Every option is equally settable as configuration — this is not a parallel parser but the same keys
the aliases point at, which is how the other sources reach it:

```bash
--Mosaic:TilesAcross=80               # command line
Mosaic__TilesAcross=80                # environment variable
{ "Mosaic": { "TilesAcross": 80 } }   # appsettings.json
```

The two quality knobs pull against each other: raising `ColorAdjustStrength` or `TilesAcross`
improves the zoomed-out likeness, while raising `TileSize` and lowering `ColorAdjustStrength`
improves the zoomed-in detail. Output is roughly `TilesAcross × TileSize` pixels on the long edge —
watch that product.

## Commands

```bash
dotnet build                      # from the repo root; uses GridArt.sln
dotnet test                       # xUnit suite, ~1s
dotnet run --project src -- --help
```

Use `--no-launch-profile` when running with arguments, otherwise `launchSettings.json` chimes in
with extra output.

## The tile cache

Decoding a folder of full-size photos and resampling each to cell size is the dominant cost of a run
and is fully deterministic, so the resized pixels are cached in
`%LOCALAPPDATA%\GridArt\cache\tiles-<folder-hash>-t<TileSize>-v<FormatVersion>.bin`. Measured on 300
1200×900 JPEGs: 0.6s cold, 0.2s warm.

**The cache key covers only what the cached pixels depend on:** the tiles folder, `TileSize`, the
per-file identity (path + length + last-write time), and `FormatVersion`.

Everything else is deliberately *excluded*, because it changes how tiles are selected or composited,
not what a decoded tile looks like: `TilesAcross`, `SignatureGrid`, `ColorAdjustStrength`,
`MaxTileReuse`, `RepeatAvoidanceRadius`, `Recursive`, and the base image. Adding any of those to the
key would make every parameter tweak pay full decode cost for nothing —
`Options_unrelated_to_tile_pixels_still_hit_the_cache` pins this.

Rules when touching this code:

- **Bump `FormatVersion` if the produced pixels could change** — resize sampler, crop mode, anchor,
  pixel format, or the file layout. It is part of the filename, so old entries are ignored rather
  than silently reused. Forgetting this is the one way to get a *wrong* mosaic from the cache rather
  than a slow one.
- **Signatures are never cached**, only pixels. Fingerprinting a cell-sized bitmap is trivially cheap,
  and recomputing it keeps `SignatureGrid` out of the key.
- **The cache must never fail a run.** Every read/write error is swallowed and logged at debug, then
  the tile is decoded normally. A corrupt cache file is treated as empty
  (`Corrupt_cache_file_falls_back_to_decoding`).
- **Staleness is length + last-write time**, the same signal MSBuild uses — not content hashing,
  which would mean reading every byte and defeat the purpose. A file rewritten with an identical
  length *and* timestamp would go unnoticed; `--clear-cache` is the escape hatch.
- **`entries` is a `ConcurrentDictionary`.** Tiles load in parallel, so `TryGet` reads while other
  threads `Set`. A plain `Dictionary` read concurrently with a write is undefined behaviour, not just
  a lost update.
- Saves are atomic (temp file + `File.Move`), so a crash or concurrent run cannot leave a torn cache.
- Entries for files no longer in the folder are pruned on save; the file is not rewritten when
  nothing changed.

The invariant a cache must satisfy is that it changes only speed:
`Cache_does_not_change_the_output` asserts cold, warm and `--no-cache` runs are byte-identical.

## Conventions that matter here

- **Do all colour maths in linear light, never in gamma-encoded sRGB.** Averaging sRGB bytes makes
  every average too dark; `ColorSignature` averages linearly and only converts to CIELAB at the end.
  Use `ColorMath`, don't hand-roll a conversion.
- **Match perceptually.** Distances are CIELAB ΔE, not RGB Euclidean. Squared ΔE is kept through the
  inner matching loop to avoid square roots; take the root only when reporting.
- **Derive grid boundaries with scaled integer division** (`i * total / n`), so cells tile the source
  exactly and no row or column of pixels is dropped or double-counted. Don't use a rounded cell
  width multiplied by an index.
- **Keep the aspect ratio.** `MosaicBuilder.ResolveGrid` lays tiles along the long axis and derives
  the short one. A mosaic that doesn't match the base image's proportions is a bug.
- **Skip bad tile files, don't fail the run.** Tile folders are real photo libraries with stray
  files; log a warning and continue. Failing only makes sense when *no* image could be decoded.
- **Dispose images.** `Tile`, `TileLibrary` and `MosaicResult` are all `IDisposable`; ImageSharp
  buffers are large. On a throw after allocating an image, dispose it before rethrowing.
- **Adding an option means two edits:** the property on `MosaicOptions`, and an alias entry in
  `CommandLine.BuildSwitchMappings`. Without the alias it still works as `--Mosaic:Name=value`, and
  the `Every_option_has_a_long_alias` test fails to remind you. Aliases are the only hand-maintained
  part of the options surface.
- **Validate unknown switches explicitly.** The configuration provider rejects `-Z=9` but *silently
  discards* `-Z 9`, so a typo would otherwise run with unintended defaults.
  `CommandLine.FindUnknownSwitch` closes that gap and runs before the host is built. Keep its
  mappings case-insensitive to match the provider's own comparison.
- **A leading `/` is a path here, not a switch.** This was a real bug: treating `/foo/bar.png` as a
  Windows-style `/switch` made the worker reject valid MSYS/Git Bash paths as unknown options and
  print its usage, looking completely broken. `IsSwitch` only accepts `/` when the token names a
  known switch or starts with `/Mosaic:`. `IsSwitch` and `FindUnknownSwitch` must agree on this —
  they diverged once and `Windows_slash_switches_still_work` caught it.
- **Argument errors must name the problem.** Say which argument is missing and echo what was
  received, and flag an "unknown option" that is actually an existing path. Printing bare usage gives
  the user no way to tell a forgotten argument from a swallowed path.
- Errors reaching the user are `logger.LogError("{Message}", ex.Message)` with the stack trace at
  debug level, so normal runs stay readable. Messages should say what to change (which option, which
  path), as the `MaxTileReuse` and `Overwrite` errors do.
- File-scoped namespaces, primary constructors, nullable enabled, implicit usings. Match the
  surrounding style; comments explain *why*, not *what*.

## Dependencies

- **SixLabors.ImageSharp is pinned to 3.1.x on purpose.** 4.0 added a build-time paid-license gate
  that fails the build without a Six Labors licence key. Do not bump the major version unless a
  licence has actually been acquired.
- `Microsoft.Extensions.Options.DataAnnotations` provides `ValidateDataAnnotations()` for the
  `[Range]`/`[Required]` attributes on `MosaicOptions`.

## Testing expectations

Tests generate their own fixtures — no binary assets are committed. The suite asserts the *actual
product claim*, not just that code runs:

- downscaling the mosaic ("zooming out") lands within a ΔE budget of the downscaled base image,
- cells retain internal contrast ("zooming in" still shows pictures, not flat colour),
- `ColorAdjustStrength = 1` reproduces the base image, confirming the blend endpoints,
- linear-light averaging is verified against a known value (half black + half white → sRGB ~188).

CLI behaviour is tested by launching the built assembly as a real process (`RunCliAsync`), because
`Program.cs` holds the configuration wiring and is not otherwise injectable. Those tests assert that
short aliases and `--Mosaic:*` keys produce byte-identical output.

When you change matching or rendering, keep those invariants covered. `InternalsVisibleTo` in
`src/gridart.csproj` exposes internals such as `MosaicBuilder.ResolveGrid` to the test project.

## Known limitations

- Matching is a single greedy pass in raster order, not a global optimal assignment. It is fast and
  deterministic, but a cell late in the scan can be left with weaker choices under a low
  `MaxTileReuse`.
- Tiles are centre-cropped to a square cell, so off-centre subjects can be clipped.
- The whole mosaic is held in memory; `TilesAcross × TileSize` beyond ~20000px on an edge will be
  memory-hungry.
