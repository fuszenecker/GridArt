# AGENTS.md

Guidance for AI agents working in this repository.

## What this project is

GridArt is a .NET worker that builds **photomosaics**. Given a base image and a folder of
additional images, it produces a new image that:

- **reproduces the base image when viewed zoomed out** (or downscaled), and
- **resolves into the individual source images when zoomed in**.

The output keeps the base image's aspect ratio, and so does every individual tile — the cells are
miniatures of the base image's shape, not squares. Each source image is used at most once by default.
"Looks like the base image" is not a vibe — it is measured as a mean CIE76 ΔE between corresponding
cells of the mosaic and the base image, reported at the end of every run.

## Layout

| Path | Purpose |
| --- | --- |
| `GridArt.slnx` | Solution; both projects are attached to it |
| `src/gridart.csproj` | The worker (`Microsoft.NET.Sdk.Worker`, net10.0) |
| `src/Program.cs` | Host wiring, config binding, usage guard |
| `src/CommandLine.cs` | Positional args, short/long aliases, unknown-switch check, usage text |
| `src/MosaicOptions.cs` | Every tunable, bound from the `Mosaic` config section |
| `src/Worker.cs` | Runs one job, saves the output, sets the exit code, stops the host |
| `src/Progress/IProgressReporter.cs` | Phase-progress abstraction plus the no-op used by `--quiet` and tests |
| `src/Progress/LoggingProgressReporter.cs` | The only implementation: reports phases through `ILogger` |
| `src/Imaging/MosaicBuilder.cs` | Grid sizing, cell matching, rendering, colour adjust, quality scoring |
| `src/Imaging/TileLibrary.cs` | Parallel tile loading, resize + centre-crop, fingerprinting |
| `src/Imaging/TileCache.cs` | Append-as-you-go on-disk cache of decoded, cell-sized tile pixels |
| `src/Imaging/StageSchedule.cs` | When the next intermediate stage image is due, and its backoff |
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
| `-n` | `--tiles-across` | `TilesAcross` | 64 | Tiles **per axis**; the grid is `n × n` because the cells carry the aspect ratio, see below |
| `-s` | `--tile-size` | `TileSize` | 48 | Pixels along a tile's **longer** edge; the shorter edge follows the base ratio |
| `-g` | `--signature-grid` | `SignatureGrid` | 3 | Match patches per axis; 1 = average colour only, >1 also matches structure |
| `-c` | `--color-adjust` | `ColorAdjustStrength` | **0 (off)** | 0 = untouched tiles, 1 = exactly the base image |
| `-r` | `--max-reuse` | `MaxTileReuse` | **1 (no reuse)** | Cap on placements per source image; 0 = unlimited, see below |
| `-d` | `--repeat-distance` | `RepeatAvoidanceRadius` | 2 | Cells that must separate two uses of one image — never relaxed, see below |
| | `--recursive` | `Recursive` | true | Scan subfolders of the tiles folder; bare, or `--recursive false` |
| `-f` | `--overwrite` | `Overwrite` | false | Replace an existing output file |
| | `--no-cache` | `NoCache` | false | Skip the decoded-tile cache for this run |
| | `--cache-dir` | `CacheDirectory` | `%LOCALAPPDATA%\GridArt\cache` | Cache location |
| | `--clear-cache` | `ClearCache` | false | Delete all cache files before running |
| | `--stage-interval` | `StageIntervalSeconds` | 60 | Seconds between intermediate stage images; 0 disables them |
| `-q` | `--quiet` | `Quiet` | false | Drop the per-phase progress lines; results, warnings and errors still log |

Every option is equally settable as configuration — this is not a parallel parser but the same keys
the aliases point at, which is how the other sources reach it:

```bash
--Mosaic:TilesAcross=80               # command line
Mosaic__TilesAcross=80                # environment variable
{ "Mosaic": { "TilesAcross": 80 } }   # appsettings.json
```

The two quality knobs pull against each other: raising `ColorAdjustStrength` or `TilesAcross`
improves the zoomed-out likeness, while raising `TileSize` and lowering `ColorAdjustStrength`
improves the zoomed-in detail. `ColorAdjustStrength` is 0 by default — the likeness comes from
matching alone unless you ask for tinting. Output is `TilesAcross × TileSize` pixels on the long edge —
watch that product.

`TilesAcross` also sets how many photos the folder must hold, because reuse is off by default: `n × n`
cells need `n²` images, so the default 64 needs 4,096 and 120 needs 14,400. Too few and the run refuses
up front, naming a grid that would fit.

## Commands

```bash
dotnet build                      # from the repo root; uses GridArt.slnx
dotnet test                       # xUnit suite, ~3s
dotnet run --project src -- --help
```

Use `--no-launch-profile` when running with arguments, otherwise `launchSettings.json` chimes in
with extra output.

## Tiles are shaped like the base image, and that forces a square grid

A cell is a **miniature of the base image's shape**, not a square. `--tile-size` sets the longer edge
and `ResolveTileSize` derives the shorter one from the base ratio: a 16:9 base at `-s 48` gives 48×27
cells, a 3:4 base gives 36×48.

**The grid is therefore square — `ResolveGrid` returns `(tilesAcross, tilesAcross)` — and that is
arithmetic, not a preference.** The output measures `columns · cellWidth` by `rows · cellHeight`.
Requiring both that the output match the base ratio *and* that `cellWidth : cellHeight` match it too
leaves exactly one solution: `columns : rows == 1 : 1`. The aspect ratio lives in the cell shape or in
the cell counts; it cannot live in both.

It used to live in the counts: a proportional grid of square cells. That output *was* correctly
proportioned, which is why it survived review — but `ResizeMode.Crop` then centre-cropped every source
photo to a square, throwing away the sides of every landscape shot and the top and bottom of every
portrait one. The mosaic was right in outline and wrong in every tile. Do not "simplify" back to
square cells; if you change `ResolveGrid`, `Tiles_are_shaped_like_the_base_image_not_square` is what
should stop you.

- **Rounding to whole pixels is the only inexactness, and it is bounded by half a pixel.** 16:9 at
  `-s 24` wants a 13.5px short edge and gets 14, so the cell is 1.714 rather than 1.778. The shape test
  asserts the *rounded* short edge exactly rather than a ratio within some slack, so the residual is
  pinned rather than hidden; at the default 48 it halves. A square base yields square cells, correctly.
- **A square grid means the output ratio is exactly the cell ratio.** Anything that reports or asserts
  output proportions can read them off one cell.
- **The cache key is the cell's width *and* height** (`FormatVersion` 3) — see "The tile cache".

## No image is reused unless there aren't enough

`-r` / `MaxTileReuse` **defaults to 1: each source image is placed at most once.** 0 means unlimited,
N caps it at N. **If the folder holds too few images to fill the grid, the cap is raised to the minimum
that covers it and the run warns — it does not refuse.**

It defaulted to 0 — unlimited — so an ordinary run reused images heavily and admitted it only in a
statistic nobody reads: 3,000 photos over a 120×90 grid produced **"715 distinct tile(s)" for 10,800
cells**, i.e. every image about fifteen times over. Reporting "0 repeat-distance violations" alongside
that number was true and beside the point; the run was reusing images wholesale and the summary did not
say so in words. "Build a mosaic from this folder of photos" reads as one photo per cell, so that is
the default.

- **`--repeat-distance` does not cover this.** It is a *local* radius: it stops a repeat appearing near
  its twin and permits one anywhere outside the radius. With the default `-r 1` it forbids nothing
  extra; it earns its keep only once reuse is allowed with `-r 0` or `-r N`. Never present the radius as
  a no-reuse guarantee.
- **A short folder reuses images rather than refusing to draw — showing a result beats avoiding
  reuse.** This is the one limit that is *not* absolute, and it is a deliberate exception to "an option
  is an instruction": with tens of thousands of files, "add more images and start over" can mean a run
  that never produces anything. `ResolveEffectiveReuse` raises the cap and the run continues. It used to
  throw.
- **The cap is raised to `ceil(cells / images)` — the smallest value that covers the grid, never to
  unlimited.** `MinimumReuseFor` is the pure arithmetic, pinned by a table test. 4,500 photos over 6,400
  cells becomes a cap of 2, not 0: almost every photo still appears once. Unlimited reuse is what
  produced "715 distinct tiles for 10,800 cells", and the distinction between "reuse when you must" and
  "reuse without limit" is the entire reason this returns a number.
- **Every raised cap is reported at warning level, naming the shortfall and the new cap.** A fallback
  that happened quietly would be the old defect wearing a new hat: it is acceptable *only* because it is
  stated in words every time. The warning also admits what the number does not promise — matching is
  greedy, so some images go unused while others hit the cap (4,500 photos over 6,400 cells yields ~3,270
  distinct, not 3,200).
- **Report reuse in words in the final summary too.** `Mosaic built … 3,270 distinct tile(s) for 6,400
  cells (3,130 cell(s) reuse an image already placed)` — a bare `DistinctTiles` count *is* the reuse, and
  a count is not a sentence.
- **Stages raise the cap silently (debug, not warning).** A preview from the first 200 tiles must reuse
  them heavily, and that is the point of stages; warning on every one would bury the final mosaic's
  warning, which is the one that means something. Stages still skip for the repeat distance, which needs
  very few images.
- **Tests that are not about placement may still pass `MaxTileReuse = 0` / `-r 0`.** No longer
  load-bearing now that the builder raises the cap itself, but it keeps a fixture's output independent
  of whatever the fallback computes, and it states the intent outright.

## Never change a source image's colours

A tile must reach the output with the colours of the photo it came from. Three separate defects each
broke that, and all three are fixed — none of them may come back.

- **Every `Resize` sets `Compand = true`.** Without it the resampler averages *gamma-encoded* bytes,
  which is mathematically the wrong average and makes every downscaled tile darker than its source.
  Measured over 3,000 real photos resized 32×32: source mean linear luma **0.3118**; with
  `Compand = true` **0.3119** (delta **+0.0001**, worst tile −0.0025); with `Compand = false`
  **0.2150** (delta **−0.0968**, worst tile **−0.2805**). As sRGB grey that is 152 → 128 — a visible,
  uniform darkening of the whole mosaic. Both resizes need it: the tiles in `TileLibrary` *and* the
  base rescale in `MosaicBuilder.AnalyseBaseAsync`.
- **`Render` copies pixel rows verbatim; it does not use `DrawImage`.** `DrawImage` blends the tile
  against the blank canvas, which rewrites the colour channels of a fully transparent source pixel to
  zero — measured: `Rgba32(200,40,40,0)` in, `Rgba32(0,0,0,0)` out. A `Span.CopyTo` per row is both
  faithful and faster. Cell rows write disjoint pixel rows, which is what makes it parallel-safe.
- **`ApplyColorAdjust` preserves the tile's own alpha.** It used to write `byte.MaxValue`, turning a
  transparent PNG tile into a solid block. Only the colour channels are blended, and only in linear
  light.
- **`ColorAdjustStrength` defaults to 0, and must keep defaulting to 0.** It shipped at 0.35, which
  tinted every tile of every default run 35% toward the base colour — the mosaic was built from
  recoloured copies of the photos rather than the photos, and nobody asked for that. Tinting is a
  legitimate thing to want, so the option stays; it is opt-in. A better-looking likeness is not a
  reason to give this a non-zero default again.

The end-to-end check that pins all of this: with `-c 0`, **all 10,800** 32×32 blocks of a rendered
3,000-tile mosaic hash byte-identical to a resized source image, 0 altered. If you touch the resize,
the render or the adjust, re-run that comparison rather than eyeballing the output. In the suite,
`Default_run_reproduces_every_source_image_pixel_for_pixel` is the same assertion in miniature and
runs on **default options**, so it fails if any of the four defects returns *or* if the default
strength drifts off 0; `Color_adjustment_is_off_unless_it_is_asked_for` pins the default on its own.

## Repeat distance is a hard constraint

`-d` / `RepeatAvoidanceRadius` means **no two cells within that Chebyshev radius may use the same
source image.** It is an exclusion from the candidate set, not a term in the score.

It was originally a `RepeatPenalty = 900f` added to a losing candidate's squared-ΔE score, which is a
*preference*: whenever every alternative scored more than 900 worse, the repeat still won and a tile
landed next to itself. Measured on a flat grey 20×20 grid with 300 unique tiles, the penalty version
produced **1,859 violations at `-d 6`** (and 108 at `-d 4` on an aliased fixture); the exclusion
version produces **0 at `-d 1`, `-d 2`, `-d 4` and `-d 6`**. Do not reintroduce a scoring penalty here.

- **It is never relaxed, for any cell, for any reason.** An earlier version shrank the radius one ring
  at a time for whichever cells had no legal move and logged a warning about it. That is still
  repetition: "no repetition" that produces repetitions as long as it mentions them is not the
  constraint. The relaxation loop is gone. `BuildAsync` instead checks satisfiability *before* placing
  anything and throws if the folder is too small, naming the number of images that would be enough:
  `--repeat-distance 3 needs at least 25 distinct image(s) for a 10x10 grid, but only 6 loaded.`
  Refusing to start is the correct answer; a mosaic that quietly breaks the rule it was given is not.
- **`MinimumTilesForRepeatDistance(columns, rows, radius)` is exact, not a heuristic.** Cells fill in
  raster order, so the ban list for any cell is at most `radius` full rows above it (each
  `2·radius+1` cells wide, clamped to `columns`) plus `radius` cells to its left. One image more than
  that always leaves a legal choice, which is what makes the check sound enough to throw on. It is
  necessary *and* sufficient — `Enough_tiles_for_the_computed_minimum_always_succeeds` builds with
  exactly the stated minimum on a flat base, the worst case, and asserts zero violations. If that test
  ever throws, the formula undercounts; do not "fix" it by relaxing the radius.
- **`Assign` throws rather than placing a repeat.** Reaching a cell with no admissible tile after the
  up-front check means either a `--max-reuse` exhaustion or a bug. Both are errors. There is no
  fallback branch, and adding one would reintroduce the defect above.
- **`IsUsedNearby` only scans already-assigned cells** — rows above, plus the current row up to the
  previous column — because raster order means later cells hold nothing yet. That is still symmetric in
  effect: if A rejects B, B was placed first and A moved, so no surviving pair inside the radius is
  equal. It does not need a forward scan; it needs the caller to exclude rather than penalise.
- **Stages skip rather than relax.** A preview built from the first 200 tiles usually cannot honour a
  radius meant for 20,000, so `StageWriter` tests the same `MinimumTilesForRepeatDistance` (and the
  reuse cap) and returns false when the tiles loaded so far are not enough — no stage that run, instead
  of a stage that violates the rule. Previews start appearing once enough tiles have loaded.
- Tests assert the guarantee over **every cell pair within the radius**, from the rendered pixels, not
  `DistinctTiles > 1`. The old assertion passed for years against the broken penalty because using two
  tiles somewhere satisfies it. `Repeat_distance_zero_allows_repeats` pins the other direction, and
  `Too_few_tiles_fails_instead_of_relaxing_the_repeat_distance` pins the refusal.
- **Tests that are not about placement opt out explicitly with `RepeatAvoidanceRadius = 0` / `-d 0`.**
  Small fixtures cannot satisfy the default radius 2 on a 10×10 grid (that needs 13 distinct images),
  and since the constraint is never relaxed, such a run now fails by design. Opting out is a one-line
  statement of intent; loosening the constraint to keep fixtures alive is not an option.
- **Repeat-distance tests must use `CreateDistinctTileFolder`, not `CreateTileFolder`.** The latter
  cycles 8 palette colours × 4 brightness steps, so it only yields 32 distinct appearances and tile 000
  is pixel-identical to tile 032 — two different files that render identically read as a violation the
  matcher never committed. This cost a debugging cycle; the fixture, not the code, was wrong.

## Intermediate stage images

A run over tens of thousands of images spends most of its time loading tiles, so every
`StageIntervalSeconds` a preview mosaic is written as `<output>.stage-NNN.<ext>` beside the real
output. Stage 1 built from 200 tiles looks blocky, stage 6 from 20,000 looks nearly final — that
progression is the point.

Base analysis and tile loading run **concurrently** (see "Parallelism"), so a stage can fall due before
the cell signatures exist. `StageWriter` therefore takes the `Task<BaseAnalysis>` rather than its
result and awaits it *inside* the stage claim: the claim is exclusive, so at most one loader thread ever
parks there. Don't "fix" that into a `IsCompletedSuccessfully` check-and-skip — it drops every early
stage, and on a short run that means no previews at all.

- **A stage must never change the final mosaic.** `Assign` keeps its use counts in a local array rather
  than on `Tile`, which is what makes calling it twice safe; a stage only reads tile pixels.
  `Stages_do_not_change_the_final_mosaic` compares a staged run against an unstaged one byte for byte.
  If you add state to `Tile` or to matching, keep it out of `Assign`.
- **A stage is a preview, not a small mosaic.** Likeness is not scored, and tiles arrive in load order,
  so stages are not reproducible. The final mosaic is — `TileLibrary` sorts by path before returning,
  because tile order decides ties in matching.
- **A stage obeys `--max-reuse` too; it is skipped when it cannot.** It used to pass `maxTileReuse: 0`,
  i.e. quietly ignore the cap. The options are instructions, not hints: when too few tiles have loaded
  to fill the grid inside the cap, the stage is skipped (its number is given back) and the next one gets
  it right. Never break a stated limit to produce a picture.
- **Only one stage renders at a time, and it backs off by what it cost.** The due check runs on
  whichever loader thread reaches it first, so `StageSchedule.TryClaim`/`Release` is the mutual
  exclusion. `BackoffFactor` caps stages at 1/4 of wall clock, so a 20s render on a huge grid settles
  to one every ~80s instead of saturating a 60s interval.
- **A failed stage is a warning, never an exception.** Losing a preview must not kill a run that is
  already minutes in (`Stages_do_not_break_a_run_that_cannot_write_them`).
- Stage files are numbered consecutively from 1; a claim that produces nothing gives its number back.
  Stages are written before the Worker saves the real output, so the writer creates the output
  directory itself.

## The tile cache

Decoding a folder of full-size photos and resampling each to cell size is the dominant cost of a run
and is fully deterministic, so the resized pixels are cached in
`%LOCALAPPDATA%\GridArt\cache\tiles-<folder-hash>-t<cellW>x<cellH>-v<FormatVersion>.bin`. Measured on
3,000 photos at a 32px cell: loading takes **502ms** cold and **51ms** warm, for a 12.4 MB cache file.

**Entries are appended as each tile decodes, not collected and written at the end.** With tens of
thousands of images a cold run takes many minutes, and a single write at the end means a run
interrupted at minute nine — Ctrl-C, a crash, a full disk — leaves *nothing* and starts from zero next
time. Verified: a `kill -9` 0.35s into a 3,000-tile run left a 2.9 MB cache (of 12.4 MB full) that the
next run reused for exactly 717 tiles, producing a mosaic byte-identical to one built from a full cache.

### The file format (v3)

Little-endian, written with `BinaryWriter`. A 16-byte header, then one record per tile, then nothing —
**no count, no index, no footer**, which is what makes a new entry a single append:

```
header  uint32  Magic         0x47524441  "GRDA"
        int32   FormatVersion 3
        int32   cellWidth     so a file written for another cell shape is rejected outright
        int32   cellHeight

record  uint32  RecordMarker  0x54494C45  "TILE"
        string  path          BinaryWriter 7-bit-encoded length prefix, UTF-8, absolute
        int64   length        source file size, for staleness
        int64   ticks         source LastWriteTimeUtc.Ticks, for staleness
        byte[]  pixels        cellWidth * cellHeight * 4, raw RGBA, no compression
```

A record is `4 + (1..2 + bytes(path)) + 8 + 8 + cellWidth·cellHeight·4` bytes — 4,117 plus the path at
a 32×32 cell. Duplicate paths are legal and **last one wins**; that is how a re-decoded stale entry
supersedes its predecessor without a rewrite, and `Save` compacts them away later.

v3 replaced v2's single square `tileSize` with a width and a height, in both the header and the
filename, because cells now take the base image's aspect ratio. A v2 file holds square pixels of the
wrong shape for any non-square base, so it must not be reused — the version bump is what guarantees
that, and it is why the dimensions belong in the key at all.

**Yes, the run is interruptible, and the cache is why.** Reading stops at the first byte that is not a
valid record, so a half-written tail from a `kill -9` is simply dropped and everything before it is
kept; the next append truncates to that offset before writing. Verified: a hard kill 0.35s into a
3,000-tile run left a 2.9 MB cache that the next run reused for exactly **717 tiles**, and that run
produced a mosaic **byte-identical** to one built from a full cache. Cancellation itself is a
`CancellationToken` threaded from `Worker.ExecuteAsync` through every phase and every parallel loop.

**The cache key covers only what the cached pixels depend on:** the tiles folder, the cell width and
height, the per-file identity (path + length + last-write time), and `FormatVersion`. `TileSize` alone
is not enough — the cell shape also depends on the base image's aspect ratio, so a 48×27 cell and a
48×48 one must not share a file even though both came from `-s 48`.

Everything else is deliberately *excluded*, because it changes how tiles are selected or composited,
not what a decoded tile looks like: `TilesAcross`, `SignatureGrid`, `ColorAdjustStrength`,
`MaxTileReuse`, `RepeatAvoidanceRadius`, `Recursive`, and the base image. Adding any of those to the
key would make every parameter tweak pay full decode cost for nothing —
`Options_unrelated_to_tile_pixels_still_hit_the_cache` pins this.

Rules when touching this code:

- **The file format is an append-friendly record stream: header, then records, no count.** A count in
  the header would force a full rewrite per entry, which is exactly what appending exists to avoid.
  Every record starts with `RecordMarker` so a torn tail left by a killed process is recognised as
  garbage instead of being read as a string length; the reader returns the offset of the last complete
  record and the next append truncates to it. `A_torn_final_record_is_dropped_and_the_rest_survives`
  and `Appending_after_a_torn_record_overwrites_the_garbage` pin this.
- **`Save` is now a compaction step, not the write.** It returns false and touches nothing in the
  common case (`Save_does_not_rewrite_the_file_when_every_entry_was_appended`); it only rewrites when
  entries must be pruned or a stale entry was re-decoded and superseded on disk. If you make it write
  unconditionally you have restored the multi-megabyte end-of-run stall.
- **Compare paths as full paths, always.** Entries are keyed on `FileInfo.FullName`, but `Save`'s live
  list holds paths as they were enumerated — relative whenever the tiles folder was a relative argument,
  which is the normal way to invoke the tool (`gridart base.png tiles`). Comparing the two forms
  directly made every entry look deleted, so `Save` pruned the whole cache and rewrote it as a 12-byte
  header: **the cache never hit once, on any relative run**, while the unit tests (which use absolute
  temp paths) all passed. `Save_keeps_entries_whose_live_paths_arrive_relative` pins it. The general
  lesson: a cache that silently misses looks exactly like a slow program, so verify hits by their
  logged count (`Prepared N tile(s) …, M from cache`), not by the absence of errors.
- **Appends are flushed per record but not `Flush(true)`.** Forcing the platter on every tile would
  cost more than the decode it protects, and a torn tail is already recoverable.
- **Reads open with `FileShare.ReadWrite`.** Another process may be appending; a plain `File.OpenRead`
  collides with its write handle and the cache looks unusable. This was a real failure, caught by
  `Cache_entries_are_readable_before_Save_is_called`.
- **`TileCache` is `IDisposable`** — it holds an append handle open for the run.
- **Bump `FormatVersion` if the produced pixels could change** — resize sampler, crop mode, anchor,
  pixel format, or the file layout. It is part of the filename, so old entries are ignored rather
  than silently reused. Forgetting this is the one way to get a *wrong* mosaic from the cache rather
  than a slow one. (v1 → v2 was the move to appendable records; v2 → v3 the move from a square
  `tileSize` to a cell width and height.)
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

## Parallelism

Every phase that can use the whole machine does. `MosaicBuilder.CpuBound` is the single place the
degree of parallelism is set (`Environment.ProcessorCount`), and it carries the cancellation token, so
a `Ctrl-C` stops mid-phase instead of at the next phase boundary.

What runs in parallel, and why each is safe:

| Phase | How | Why it is safe |
| --- | --- | --- |
| Base analysis ‖ tile loading | `Task.WhenAll` in `BuildAsync` | Cell signatures depend only on the base image; cached/resized tiles only on the cell size. Nothing is shared. `Image.IdentifyAsync` reads the base *header* first so the cell shape is known without decoding, which is what keeps the overlap. |
| `Analysing base image` | `Parallel.For` over cell rows | Read-only on the base image; each row writes only its own signature slots. |
| `Loading tiles` | `Parallel.ForEachAsync` | Decoding is CPU-bound and per-file; the shared list is under a `Lock`, the cache is a `ConcurrentDictionary` with a file lock for appends. |
| `Matching tiles` (distances) | `Parallel.For` over a block of cells | A cell's distance to every tile is independent of what other cells chose. |
| `Rendering mosaic` | `Parallel.For` over cell rows | Each cell row writes a disjoint band of pixel rows. |
| `Colour matching` | `Parallel.For` over pixel rows | One row in, one row out, no cross-row reads. |
| `Scoring likeness` | Two `Task.Run` signature passes, then `Parallel.For` over deltas | The two passes read different images and share nothing; each is internally parallel and they simply share the cores. |

Rules:

- **The pick in `Assign` is sequential and must stay that way.** The reuse cap and the repeat-distance
  exclusion both depend on the cells already placed, so choosing cell *n* is genuinely ordered. Only
  the distance computation parallelises. Making the pick concurrent would make output
  non-reproducible *and* break `-d`.
- **Distances are computed in blocks, not all at once** (`DistanceBlockFloats` = 8 Mi floats / 32 MB,
  capped at `DistanceBlockCells` = 4096 cells). The full cells × tiles matrix is not an option: 100k
  cells × 30k tiles is 12 GB. The block keeps the buffer bounded and cache-resident while still
  feeding every core.
- **`DangerousGetPixelRowMemory` lives on `ImageFrame<T>`, not `Image<T>`** — go via
  `.Frames.RootFrame` (or `using SixLabors.ImageSharp.Advanced;`). Concurrent read-only access and
  concurrent writes to *distinct* rows of one image were both verified to work.
- **Anything the parallel phases mutate is per-index or interlocked.** Don't add a shared accumulator
  to one of these loops without one.
- Measured on 3,000 tiles into a 120×90 grid (10,800 cells, 3840×2880 output) on 16 cores:
  cold run **0.8s total** — loading 502ms, matching 230ms, rendering 21ms, colour matching 11ms,
  scoring 33ms. Warm cache: loading **51ms**, total 0.6s.

## Progress reporting

A run has long silent stretches (loading 1200 tiles, matching 100k cells), so every phase reports
through `IProgressReporter`: a start line, throttled interim updates, and a completion line with the
count, duration and rate.

The phases, in order: `Reading base image`, `Rescaling base image`, `Analysing base image`, `Scanning
folder`, `Loading tiles` (with `Stage N from … tile(s)` interleaved), `Finalising tile cache`,
`Matching tiles`, `Rendering mosaic`, `Colour matching`, `Scoring likeness`, `Saving <file>`.

- **Progress goes through `Microsoft.Extensions.Logging`, never to `Console` directly.** An earlier
  version drew an in-place `\r` bar on stderr; that was removed. Going through `ILogger` means
  log-level filters, `--quiet` and any additional provider (file, Seq, OpenTelemetry) apply to
  progress the same as to everything else, and the output stays intact when redirected to a file.
  Consequences to preserve: no carriage returns, no cursor control, no ANSI, and one self-contained
  record per update.
- **Values are named, not interpolated** — `{Phase}`, `{Current}`, `{Total}`, `{Percent}`,
  `{Elapsed}` — so a structured sink can query them. Don't repeat a placeholder name within one
  template; names collide in a structured sink (which is why the rate is `{Rate:N0}/s`, with the unit
  carried by the neighbouring `{Unit}`).
- **Interim updates are throttled on time, not on count** (`DefaultUpdateInterval`, 1s). Every update
  is a real log record, so a 100k-cell phase must produce a handful of lines, not 100k. `Advance`
  checks `IsEnabled` before doing any formatting work.
- **`Advance` is called from inside `Parallel.ForEachAsync`** (tile loading), so the counter is
  `Interlocked` and only the thread that wins a `CompareExchange` on the report timestamp logs.
- A phase that never calls `Advance` is one indivisible step (decoding a single image); it reports
  bare elapsed time rather than "0 items". A rate is only quoted once elapsed ≥ 0.1s, where it means
  something.
- `Dispose` is idempotent — `TileLibrary` disposes its load phase explicitly *and* via `using`.
- `--quiet` swaps in `NullProgressReporter`; it silences progress only, not results or warnings.
  Tests default to it, so `IProgressReporter` is an optional trailing parameter throughout.

## Conventions that matter here

- **Do all colour maths in linear light, never in gamma-encoded sRGB.** Averaging sRGB bytes makes
  every average too dark; `ColorSignature` averages linearly and only converts to CIELAB at the end.
  Use `ColorMath`, don't hand-roll a conversion. This applies to resampling as well — see "Never
  change a source image's colours".
- **An option is an instruction, not a preference.** `--repeat-distance` is an exclusion rather than a
  score penalty, not a term in the score, and when it cannot be met the run **fails with a message
  saying what to change** — it does not produce an image that breaks the rule and mention it in a
  warning. Defaults follow from the same principle: nothing that alters the source images happens unless
  it was asked for, which is why `ColorAdjustStrength` is 0 and `MaxTileReuse` is 1.
- **`--max-reuse` is the one documented exception, because refusing to draw is itself a failure.** A
  folder too small for the grid gets a raised cap and a warning rather than an error: on a run over tens
  of thousands of files, "add more images and start over" costs more than the reuse does. The exception
  is narrow and stays narrow — the cap goes up by the *minimum* that covers the grid, and the run says so
  every time. Colour fidelity has no such exception: there is no result worth showing that misrepresents
  the source photos.
- **A default is a promise about the obvious reading of the request.** Both defaults that shipped wrong
  — a 0.35 tint and unlimited reuse — were defensible as *pictures* and indefensible as *answers to
  what was asked*: build a mosaic out of these photos, not out of recoloured copies of a seventh of
  them. When a default trades fidelity for a nicer-looking result, it is the wrong default.
- **Say what happened in words, not only in a statistic.** "715 distinct tile(s)" was a truthful
  report of heavy reuse that nobody could be expected to read as one. Reporting a narrow check as
  though it settled a broader question is the same failure. If a run did something the user did not
  ask for, the summary has to name it.
- **Match perceptually.** Distances are CIELAB ΔE, not RGB Euclidean. Squared ΔE is kept through the
  inner matching loop to avoid square roots; take the root only when reporting.
- **Derive grid boundaries with scaled integer division** (`i * total / n`), so cells tile the source
  exactly and no row or column of pixels is dropped or double-counted. Don't use a rounded cell
  width multiplied by an index.
- **Keep the aspect ratio, in the tiles as well as the output.** `ResolveTileSize` shapes each cell
  like the base image and `ResolveGrid` is square as a consequence — see "Tiles are shaped like the base
  image". A mosaic that doesn't match the base image's proportions is a bug; so is one that matches them
  by centre-cropping every photo to a square.
- **Skip bad tile files, don't fail the run.** Tile folders are real photo libraries with stray
  files; log a warning and continue. Failing only makes sense when *no* image could be decoded.
- **Dispose images.** `Tile`, `TileLibrary` and `MosaicResult` are all `IDisposable`; ImageSharp
  buffers are large. On a throw after allocating an image, dispose it before rethrowing.
- **Adding an option means two edits:** the property on `MosaicOptions`, and an alias entry in
  `CommandLine.BuildSwitchMappings`. Without the alias it still works as `--Mosaic:Name=value`, and
  the `Every_option_has_a_long_alias` test fails to remind you. Aliases are the only hand-maintained
  part of the options surface.
- **Every `bool` option must be listed in `CommandLine.BooleanFlags`.** Adding one to `MosaicOptions`
  is three edits, not two. `--recursive` was missing from the list, so it fell through to the
  "`--key value`" branch and swallowed the token after it: `gridart base tiles --recursive -n 120`
  bound `-n` to `Mosaic:Recursive` and aborted with *Failed to convert configuration value '-n'*.
  Being on the list still allows `--recursive false` — `Parse` consumes a following token only when
  `bool.TryParse` accepts it, so a path or the next switch is left alone. That check is exactly
  `bool.TryParse` and no wider: the binder converts via `TypeDescriptor`, whose bool converter throws
  `FormatException` on `1` and `0`, so accepting those would consume the token and then kill the run.
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
- a run on **default options** renders every cell byte-identical to a resized source file, so no tint,
  gamma slip or alpha flattening can creep back in, and the default strength cannot drift off 0,
- `ColorAdjustStrength = 1` reproduces the base image, confirming the blend endpoints,
- no two cells within `--repeat-distance` share a tile — asserted per *pair* from the rendered pixels,
  on a flat base and on a gradient, and at the exact computed minimum tile count,
- too few images for the requested radius **fails** with a message naming how many are needed, rather
  than relaxing anything,
- a default run places **every source image at most once** — asserted by fingerprinting all 100 cells of
  a 10×10 grid built from exactly 100 distinct images and requiring 100 distinct fingerprints, not by
  trusting the `DistinctTiles` statistic,
- too few images for `--max-reuse` fails with a message naming the shortfall *and* a `--tiles-across`
  that would fit,
- cells are shaped like the base image across landscape, portrait, 16:9 and square bases: the long edge
  is exactly `--tile-size`, the short edge is the ideal length rounded to a whole pixel, orientation
  follows the base, and the output ratio equals the cell ratio,
- a bare boolean flag does not swallow the option after it (`--recursive -n 120`),
- linear-light averaging is verified against a known value (half black + half white → sRGB ~188).

CLI behaviour is tested by launching the built assembly as a real process (`RunCliAsync`), because
`Program.cs` holds the configuration wiring and is not otherwise injectable. Those tests assert that
short aliases and `--Mosaic:*` keys produce byte-identical output.

Progress is tested through a `CapturingLogger` fake asserting on the emitted records — level, named
values and rendered text — rather than on any console side effect. That covers the throttle, the
percentage, `SetTotal`, concurrent `Advance`, and double `Dispose`. The internal
`LoggingProgressReporter(ILogger, TimeSpan)` constructor exists so tests can shorten the update
interval instead of sleeping through it.

`StageAndIncrementalCacheTests.cs` covers the two things that make a run over tens of thousands of
images survivable, and both are asserted as observable behaviour rather than as calls made:

- the cache is readable *before* `Save` is called, a torn final record is dropped while earlier ones
  survive, the next append truncates the garbage, and `Save` leaves `LastWriteTimeUtc` untouched when
  every entry was already appended;
- a partial cache from a simulated Ctrl-C is reused *and* yields a mosaic byte-identical to one built
  from an empty cache — present-but-wrong is the failure mode worth catching;
- `Save` keeps entries when the live-path list arrives relative, which is how the tool is normally
  invoked and which no absolute-path test could have caught;
- stages appear during loading at the final dimensions, numbered consecutively from 001 beside the
  output with its extension, are absent at interval 0 or for a run shorter than the interval, do not
  change the final mosaic (byte-for-byte against an unstaged run), and do not break a run when the
  stage path cannot be written;
- `StageSchedule` is unit-tested directly for one-at-a-time claiming, giving a number back when a
  claim produced nothing, and backing off by the last stage's cost.

Its `FastStageInterval` is a *microsecond*, not a plausible-looking 0.01s: the fixtures load in a few
milliseconds, so anything a human would type leaves the run finished before the first stage is due,
and the stage tests silently pass with zero stages written.

When you change matching or rendering, keep those invariants covered. `InternalsVisibleTo` in
`src/gridart.csproj` exposes internals such as `MosaicBuilder.ResolveGrid` and `StageSchedule` to the
test project.

## Known limitations

- Matching is a single greedy pass in raster order, not a global optimal assignment. It is fast and
  deterministic, but a cell late in the scan can be left with weaker choices under a low
  `MaxTileReuse`.
- Tiles are centre-cropped to the cell, so off-centre subjects can still be clipped — much less than
  under the old square cells, but a portrait photo in a 16:9 mosaic loses its top and bottom.
- A cell's short edge is rounded to a whole pixel, so the output ratio can differ from the base ratio by
  up to half a pixel's worth (3.6% at 16:9 with `-s 24`, 1.8% at `-s 48`).
- The whole mosaic is held in memory; `TilesAcross × TileSize` beyond ~20000px on an edge will be
  memory-hungry.
