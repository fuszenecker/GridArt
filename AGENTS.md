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
| `src/CommandLine.cs` | Maps the two positional args onto config keys; usage text |
| `src/MosaicOptions.cs` | Every tunable, bound from the `Mosaic` config section |
| `src/Worker.cs` | Runs one job, saves the output, sets the exit code, stops the host |
| `src/Imaging/MosaicBuilder.cs` | Grid sizing, cell matching, rendering, colour adjust, quality scoring |
| `src/Imaging/TileLibrary.cs` | Parallel tile loading, resize + centre-crop, fingerprinting |
| `src/Imaging/ColorSignature.cs` | Per-region perceptual fingerprint used for matching |
| `src/Imaging/ColorMath.cs` | sRGB ↔ linear ↔ CIELAB conversions, ΔE |
| `tests/gridart.Tests/` | xUnit suite over generated fixtures |

## Usage

```
gridart <base-image> <tiles-folder> [options]
```

Argument 1 is the base image, argument 2 is the folder of additional images. Both are positional
and required. Any `MosaicOptions` property can be overridden with `--Mosaic:<Name>=<value>`, an
environment variable `Mosaic__<Name>`, or a `Mosaic` section in `appsettings.json`.

```bash
dotnet run --project src -- ./base.jpg ./photos \
  --Mosaic:TilesAcross=80 --Mosaic:TileSize=48 --Mosaic:Overwrite=true
```

Output defaults to `<base-image-name>.mosaic.png` beside the base image. Exit code is 0 on success,
1 on failure. `--help` prints usage and exits 0; missing positional arguments print usage to stderr
and exit 1.

### Options

| Option | Default | Effect |
| --- | --- | --- |
| `OutputPath` | `<base>.mosaic.png` | Where the mosaic is written |
| `TilesAcross` | 64 | Tiles along the **longer** axis; the short axis follows the aspect ratio |
| `TileSize` | 48 | Pixels per tile — how legible tiles are when zoomed in |
| `SignatureGrid` | 3 | Match patches per axis; 1 = average colour only, >1 also matches structure |
| `ColorAdjustStrength` | 0.35 | 0 = untouched tiles, 1 = exactly the base image |
| `MaxTileReuse` | 0 (unlimited) | Cap on placements per source image |
| `RepeatAvoidanceRadius` | 2 | Cells within which a tile is not repeated |
| `Recursive` | true | Scan subfolders of the tiles folder |
| `Overwrite` | false | Replace an existing output file |

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

When you change matching or rendering, keep those invariants covered. `InternalsVisibleTo` in
`src/gridart.csproj` exposes internals such as `MosaicBuilder.ResolveGrid` to the test project.

## Known limitations

- Matching is a single greedy pass in raster order, not a global optimal assignment. It is fast and
  deterministic, but a cell late in the scan can be left with weaker choices under a low
  `MaxTileReuse`.
- Tiles are centre-cropped to a square cell, so off-centre subjects can be clipped.
- The whole mosaic is held in memory; `TilesAcross × TileSize` beyond ~20000px on an edge will be
  memory-hungry.
