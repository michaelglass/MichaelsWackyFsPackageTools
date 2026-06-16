# SyncDocs

Keep your README and docs site in sync. SyncDocs copies marked sections from your README files into your `docs/` folder to help prevent documentation from going stale.

## How It Works

1. You add HTML comment markers around sections in your README that should be synced.
2. You add matching markers in your docs target files.
3. Run `syncdocs sync` to copy content from README to docs, or `syncdocs check` in CI to catch drift.

SyncDocs discovers sync pairs by convention -- no configuration file needed.

## Installation

```bash
dotnet tool install -g SyncDocs
```

## Usage

### Sync docs from READMEs

```bash
syncdocs sync
```

Output:

```
  README.md -> docs/index.md: updated
  src/MyLib/README.md -> docs/MyLib/index.md: in sync
```

### Check sync status (CI)

```bash
syncdocs check
```

Exits with code 0 if everything is in sync, 1 if any pair is out of sync. Use this in CI to catch forgotten doc updates.

## Marking Sections

### In your README (source)

Wrap the content you want to sync with a pair of HTML comment markers:

- **Start marker:** `<!-- sync:SECTION-NAME:start -->`
- **End marker:** `<!-- sync:SECTION-NAME:end -->`

Replace `SECTION-NAME` with any name for your section.

For example, to sync a section called "usage", you would put this in your README:

- Line 1: `<!-- sync:usage:start -->`
- Lines 2+: Your content (any markdown)
- Last line: `<!-- sync:usage:end -->`

Everything between the start and end markers will be copied to the matching target.

### In your docs file (target)

Add matching markers where the synced content should appear. Note that the target start marker omits `:start`:

- **Start marker:** `<!-- sync:SECTION-NAME -->`  (no `:start`)
- **End marker:** `<!-- sync:SECTION-NAME:end -->`

When you run `syncdocs sync`, the content between the target markers is replaced with the content from the matching source markers.

### Section names

Section names can contain letters, numbers, underscores, and hyphens. They must start with a letter or underscore:

- `usage` -- valid
- `getting-started` -- valid
- `step_2` -- valid

### Multiple sections

You can have as many synced sections as you like in a single file. Content between sections is not synced, so you can have README-only content that doesn't appear in docs.

### Full-file sync

If your README has **no** sync markers at all, SyncDocs copies the entire file content to the target. This is useful for per-project READMEs that should be mirrored exactly in docs.

## Code-Sourced Blocks (drift-proof snippets)

A README code block can be sourced from a region of a real, compiled `.fs`/`.fsx` file. The snippet then can't reference a non-existent API without breaking your build, and `syncdocs check` fails CI the moment the rendered snippet stops matching the code.

### In your README (source)

Add a `src=` attribute to the start marker, pointing at a file (relative to the repo root). The region defaults to the block name; append `#region-name` to override it. For example, replacing `START` / `END` below with real `<!-- -->` HTML comment markers:

````
[START] sync:plugin-example:start src=examples/Foo/Snippets.fs [END]
```fsharp
(this gets regenerated from the file)
```
[START] sync:plugin-example:end [END]
````

### In your `.fs`/`.fsx` file (the region)

Delimit the region with comment markers that reuse the same vocabulary:

```fsharp
// sync:plugin-example:start
let example = Foo.create "hello"
// sync:plugin-example:end
```

The lines **between** the markers (the marker lines themselves are excluded) become the block body, with the common leading indentation stripped, wrapped in a ` ```fsharp ` fence.

### Live example (dogfooded)

SyncDocs uses this feature on its own README. The block below is sourced from a region of `src/SyncDocs/Sync.fs`, so it can never drift from the real type definitions:

<!-- sync:outcome-types:start src=src/SyncDocs/Sync.fs -->
```fsharp
type SyncMode =
    | Check
    | Apply

type SyncOutcome =
    | InSync
    | OutOfSync
    | Updated
```
<!-- sync:outcome-types:end -->

### How it fits the sync chain

`syncdocs sync` runs in two stages: first it refreshes each code-sourced README block from its file region, **then** it propagates the README to `docs/` as usual — so docs pick up the refreshed snippet in a single run. `syncdocs check` reports drift (non-zero exit) if any code-sourced block is stale, and fails loudly on a missing file, a missing/duplicated region marker, or an unterminated region.

Blocks **without** a `src=` attribute behave exactly as before.

## Pair Discovery Convention

SyncDocs automatically finds sync pairs based on file location:

| Source | Target |
|--------|--------|
| `README.md` | `docs/index.md` |
| `src/MyLib/README.md` | `docs/MyLib/index.md` |
| `src/OtherTool/README.md` | `docs/OtherTool/index.md` |

Both the source and target file must exist for the pair to be discovered. If only one side of a pair exists, SyncDocs prints a helpful warning telling you which file to create:

```
  Warning: To sync docs for MyLib, create docs/MyLib/index.md
```

## Example Workflow

1. Edit your `README.md` with the latest usage instructions.
2. Run `syncdocs sync` to push changes to `docs/`.
3. Commit both files.
4. In CI, run `syncdocs check` to ensure nobody forgets step 2.

## License

MIT
