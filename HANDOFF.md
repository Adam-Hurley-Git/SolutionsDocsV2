# Handoff — Solutions Docs v2

Read this first for orientation, then [PROGRESS-LOG.md](PROGRESS-LOG.md) for full
session-by-session detail (what was built, what was tested, real bugs found and fixed).

## What this project is

Version 2 of a customized [PowerDocu](https://github.com/modery/PowerDocu) (Power
Platform solution documentation generator) pipeline. It exists because v1
(`Adam-Hurley-Git/SolutionsDocs`) edited PowerDocu's own source directly for
branding, navigation, and a rename — which drifted from upstream and coupled a UI
redesign to PowerDocu's internal HTML structure. v2 fixes that with a three-stage,
one-directional pipeline where nothing downstream can corrupt something upstream:

1. **PowerDocu** (kept true to upstream, one deliberate patch) generates Markdown +
   diagrams + Word.
2. **SharePoint enricher** (fully standalone) writes its own site/list/column data,
   never touching PowerDocu's output.
3. **Reshell tool** reads both (read-only) and builds a custom navigable/searchable
   HTML view, guaranteed to include everything by construction, not by recognition.

All three steps are self-contained `.exe`s — **nothing needs to be installed to run
the pipeline**, no PowerShell 7, no Node.js, no `PnP.PowerShell` module. The one
remaining setup item is a one-time Entra ID app registration for SharePoint sign-in
(see "SharePoint sign-in setup" below) — a Microsoft platform requirement, not
something any amount of rewriting removes.

## Where the code lives

| Repo | Visibility | Role |
|---|---|---|
| `Adam-Hurley-Git/SolutionsDocsV2` | private | Main repo. `origin`. `upstream` = `modery/PowerDocu` |
| `Adam-Hurley-Git/SolutionsDocsV2.Common` | private | The submodule at `modules\PowerDocu.Common`. `origin`. `upstream` = `modery/PowerDocu.Common` |
| `Adam-Hurley-Git/SolutionsDocsV2-Release` | **public** | Distribution only. Holds a README and release zips — plain, unencrypted, no password (deliberate choice for this early-iteration phase — see PROGRESS-LOG.md). No source. |

On the machine this was built on, the repo lives at `C:\Dev\SDV2` — **not** under
`Documents\Powerdocu\`, deliberately, to sidestep a real Windows `MAX_PATH` issue:
upstream's `examples\` folder has a ~165-character filename, so cloning into a
deep path succeeds but checkout fails with `Filename too long`. Clone this
repo to a short path (`C:\Dev\...` or similar) on any new machine.

The old v1 repos (`Adam-Hurley-Git/SolutionsDocs`, `.Common`, `-Release`) are left
completely alone — this is a fresh, separate system, not a migration in place.

## Status right now

| Piece | State |
|---|---|
| Repos & baseline | **Done.** Fresh clones, remotes repointed (`origin` = private v2 repos, `upstream` = `modery/*`), submodule wired, clean 0-error build confirmed before any edits |
| DOT-export patch | **Done.** The one deliberate deviation from upstream — 9 lines across 7 `GraphBuilder.cs`/generator files, writing the Graphviz `.dot` source alongside every `.png`/`.svg`. Nothing else in PowerDocu/PowerDocu.Common is touched |
| SharePoint enricher (Step 2) | **Code complete, builds clean against the real PnP.Framework 1.21.0 package — not exercised against a live tenant.** Non-destructive by construction (writes only new files); auth mechanism rewritten from PowerShell/PnP.PowerShell to PnP.Framework/CSOM directly in C# |
| Reshell tool (Step 3) | **Done, verified against real data.** C# console app, self-contained. Started as a Node.js prototype (`Shell/build.js`, now removed) that was tested hard against real Flow/App examples, then ported to C# with full parity confirmed (identical 141-page output, byte-for-byte identical file list) |
| PipelineUI wiring | **Done.** Three-step launcher, each step independently re-runnable once Step 1 has completed. No syntax-checked-but-unclicked risk beyond the caveat below |
| Public release | **Done.** v0.1.0 and v0.2.0 published and verified downloadable anonymously — both now superseded by the zero-prerequisite version described here, which has not yet been packaged into a release (see "Do this next") |

## Do this next

1. **Cut a new release** with the zero-prerequisite pipeline (C# reshell tool, PnP.Framework
   SharePoint enricher, no `Setup.ps1` needed). Run `PipelineUI\Build-PortablePackage.ps1`,
   zip the repo root's `Atlas PP Doc.vbs` + `PipelineUI\` folder, publish to
   `SolutionsDocsV2-Release` as a new tag (e.g. `v0.3.0`). See v0.1.0/v0.2.0's release
   notes for the exact pattern — `gh release create <tag> <zip> --title "..." --notes "..."`.
2. **Real end-to-end pipeline test** against a real Power Platform solution zip — this has
   only been verified stage-by-stage (DOT patch confirmed via build; enricher confirmed via
   clean compile only; reshell tool confirmed against a synthetic fixture built from the two
   real examples in `examples\`). No real multi-component solution zip has been available
   this whole project (see "Known gaps" below) — get one, or keep working stage-by-stage.
3. **Live-test the PnP.Framework SharePoint fetch** against a real tenant, including
   registering the required Entra ID app (see below) — this is the actual Phase-3-style gap:
   code compiles against the real package but has never talked to a real tenant.
4. **Click through the actual WPF UI** — `RunPipeline.ps1`/`MainWindow.xaml` were only
   syntax/XML-validated in this environment (no GUI available to click through). The pattern
   was mirrored carefully from the previously-working v1 launcher, but a real click-through
   on Windows hasn't happened yet.

## SharePoint sign-in setup (one-time, per tenant)

Needed before Step 2 can do anything. This is unavoidable regardless of language —
Microsoft retired the shared multi-tenant "PnP Management Shell" app in September
2024, so every tenant now needs its own Entra ID app registration. Steps (needs
"Application Developer" or "Global Administrator" role):

1. Go to <https://entra.microsoft.com> → **Entra ID → App registrations → New registration**.
   Name it anything (e.g. "Solutions Docs v2"), click **Register**. Copy the
   **Application (client) ID** — this is what the pipeline UI will ask for.
2. **Authentication** → **Add a platform** → **Mobile and desktop applications** →
   leave the checkboxes unchecked → **Custom redirect URIs**: enter `http://localhost`
   (HTTP, not HTTPS) → **Configure**.
3. **API permissions** → remove the default Microsoft Graph permissions → **Add a
   permission** → **SharePoint** → **Delegated permissions** → expand **AllSites** →
   check **AllSites.Read** → **Add permissions**.
4. Back in API permissions, click **Grant admin consent for [organization]**.

Paste the Client ID into the pipeline UI the first time Step 2 runs — it's
remembered in `%APPDATA%\PowerDocu\PipelineUI\settings.json` after that. It is not
a secret (a public app identifier, not a password/client secret), so this is safe
to store in plain text and share between machines if useful.

## Known gaps (inherited, not new)

- **No real multi-component solution zip has ever been available for this project.**
  The only zip in `examples\` (`Solution CenterofExcellenceCoreComponents_3.13_managed.zip`)
  was confirmed (in the v1 project, see its PROGRESS-LOG.md) to be pre-generated 2021 Word
  docs, not a real export — running the real generator against it produces zero output.
  All real-data testing here used the two genuinely real Markdown examples already shipped
  in `examples\` (one Flow, one App), copied into a synthetic output-folder shape.
- **No live SharePoint tenant has ever been available.** Same root cause as v1's Phase 3 —
  the free M365 Developer Program sandbox now requires a paid subscription or partner status.
  The SharePoint enricher's CSOM/PnP.Framework code compiles against the real package but
  has never made a real network call.

## Things worth remembering

- **This is a from-scratch clean clone, not v1 with edits removed.** Don't assume anything
  from the old `SolutionsDocs` repo carries over except where explicitly copied (PipelineUI's
  structure, the SharePoint enricher's detector/entity classes, the DOT patch). Branding,
  the rename, and all `HtmlBuilder.cs` customizations were deliberately left behind.
- **The DOT-export patch is the only intentional deviation from upstream PowerDocu/PowerDocu.Common.**
  If you're ever unsure whether an upstream merge will be clean, `git diff upstream/main` on
  both repos should show only that one small diff (9 lines, 7 files) plus whatever's in
  PROGRESS-LOG.md's session log since.
- **The reshell tool's core design principle: inclusion is a total function, not a pattern
  match.** Discovery is pure filesystem enumeration (no interpretation, so it can't have an
  "unrecognized, therefore dropped" branch); every file renders via an exhaustive by-type rule
  (`.md` → HTML, image → embed, anything else → download link); nav position defaults to
  mirroring the folder path. A second pass then *upgrades* whatever it recognizes (tiered
  groups, tabs, TOC, SharePoint cross-references) — upgrades only, never a gate on inclusion.
  See `PowerDocu.Shell/Program.cs`'s header comment. Don't "simplify" this back to a single
  recognition pass — that's exactly the design flaw this replaced.
- **`PowerDocu.Shell` and `PowerDocu.SharePointEnricher` are both deliberately standalone,
  not in `PowerDocu.sln`.** Same isolation reasoning as v1: neither should ever be able to
  break core PowerDocu generation, and neither should complicate pulling upstream updates.
  Build/reference them directly by `.csproj` path.
- **`assets/style.css` and `assets/nav.js` (in `PowerDocu.Shell/assets/`) came from a hand-built
  HTML/CSS mockup** (`mockup-v2/`, built in an earlier session, not part of this repo) and are
  carried over almost unchanged — they're already generic (read `window.NAV_TREE`/`SEARCH_INDEX`/
  `PAGE` at runtime), so `PowerDocu.Shell` only needed to *generate* those objects correctly,
  not touch the chrome. If the visual design ever needs to change, that's where to look.
- **A real regression was caught by diffing the C# port against the Node prototype's output**,
  not by reading the code: the SharePoint tier's CSS/icon type code came out as `"sharepoint"`
  instead of the short `"sp"` code `nav.js`/`style.css` actually key their theming off of. Fixed
  in `Program.cs`'s `KnownTiers` table. Worth remembering as a reason to always diff a port's
  output against the original, not just get it to compile.
- **`sed -i` and other line-ending-rewriting tools convert CRLF to LF silently** — same
  documented gotcha as v1's HANDOFF.md. Git will re-normalize to CRLF on commit for text files,
  which is fine for `.ps1`/`.cs`, but the `Setup.cmd`-style pitfall (cmd.exe is unreliable with
  LF-only `.cmd` files) is why any future `.cmd` file should be checked with `xxd`/`od`, not
  assumed correct just because it "looks fine" in an editor.
- **Windows Defender (or similar) can briefly lock files right after a `git clone`**, causing
  `rm -rf`/`Remove-Item` to fail with "device or resource busy" or "in use" immediately
  afterward. Not a real problem — just don't fight it; wait or work around it (this happened
  once during this project's setup and cost some back-and-forth before being identified).
- **A leftover `npx serve` process serving a parent folder can hold a lock on everything under
  it**, including a fresh git clone's `.git` folder placed inside that tree — another real
  cause of the same symptom above. Check `tasklist`/process command lines before assuming it's
  antivirus.
