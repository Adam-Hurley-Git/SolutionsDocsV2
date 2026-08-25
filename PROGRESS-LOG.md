# Solutions Docs v2 — Progress Log

Tracks implementation of the three-stage pipeline described in [HANDOFF.md](HANDOFF.md).
Read that first for orientation; this is the running "what's done, what was tested,
what changed" record — update it every session.

Repos:
- `Adam-Hurley-Git/SolutionsDocsV2` — main repo (`origin`), submodule at
  `modules\PowerDocu.Common`. `upstream` = `modery/PowerDocu`. **Private.**
- `Adam-Hurley-Git/SolutionsDocsV2.Common` — the submodule (`origin`).
  `upstream` = `modery/PowerDocu.Common`. **Private.**
- `Adam-Hurley-Git/SolutionsDocsV2-Release` — **public**. Distribution only:
  a README and release zips, no source, no password/encryption (deliberate,
  for fast iteration — see Session 1).

On the machine this was built on, the repo lives at `C:\Dev\SDV2` (not under
`Documents\Powerdocu\` where the v1 project and its mockups live) — see
Session 1 for why.

## Background: how this project started

Before this repo existed, two throwaway HTML mockups were built (outside any
git repo, under `Documents\Powerdocu\mockup\` and `mockup-v2\`) to explore a
better navigation/layout design for PowerDocu's generated HTML docs — the
real complaint being that sidebar items look identical whether they're a
same-page anchor, a sibling file, or a file in a different component's
folder. `mockup-v2` in particular was built strictly from real data (the two
examples shipped in `examples\`), field-audited against the actual C# entity
classes so nothing fabricated was presented as real. Its chrome
(`assets/style.css`, `assets/nav.js`) is what `PowerDocu.Shell` carries
forward into this repo (see Session 1). Those mockups are not part of this
repo and were never intended to be — they were disposable design exploration,
kept only as reference in the original v1 project's `PROGRESS-LOG.md`.

## Session 1 — repos, three-stage pipeline, real-data verification

**Goal, agreed with the user before writing any code:** rebuild the whole
system as a new, separate copy so v1 stays untouched, with (1) PowerDocu/
PowerDocu.Common kept unedited except one deliberate DOT-export patch,
(2) the existing PipelineUI runner carried forward, (3) a SharePoint
extractor that never destroys or edits PowerDocu's own output, and (4) a
"reshell" finishing step that wraps whatever PowerDocu + the enricher
produced in a custom navigable/searchable shell — built so it structurally
cannot lose content, not merely detects when it does. New repos, public
release, no password (explicitly requested, to allow fast iteration).

### Repos & baseline

Created `SolutionsDocsV2` (private), `SolutionsDocsV2.Common` (private),
`SolutionsDocsV2-Release` (public) via `gh repo create`. Cloned
`modery/PowerDocu` fresh — first attempt at `Documents\Powerdocu\PowerDocuV2\`
hit a real Windows `MAX_PATH` failure (`git clone` succeeds, checkout dies
with `Filename too long` — upstream's `examples\` has a ~165-character
filename); re-cloned to the short path `C:\Dev\SDV2` instead, which checked
out cleanly. Repointed remotes (`origin` = private repos, `upstream` =
`modery/*`), same pattern as v1. Submodule repointed the same way. Verified
a clean 0-error build before any edits, as the baseline checkpoint.

Two real environment issues hit during this step, worth remembering:
Windows Defender (or similar) briefly locked the failed partial clone right
after `git clone`, making `rm -rf`/`Remove-Item` fail with "device or
resource busy"/"in use" for a short time — not a real problem, just don't
fight it. Separately, a leftover `npx serve` process serving a parent folder
(from earlier mockup-testing sessions) held a lock on everything under it,
including a fresh clone's `.git` folder placed inside that tree — found via
`tasklist`/checking process command lines, killed, then the clone folder
could be removed.

### DOT-export patch (the one deviation from upstream)

Ported the same patch as v1's Phase 1: `ToDotFile(...)` alongside every
existing `ToPngFile`/`ToSvgFile` call, same filename stem. 7 real call sites
(not 8 — v1's HANDOFF.md said 8 but that included the SharePoint enricher's
own graph-patching call, which this version doesn't carry forward — see
below): `PowerDocu.FlowDocumenter/GraphBuilder.cs`,
`PowerDocu.AppDocumenter/AppDocumentationGenerator.cs` (ScreenNavigation),
`PowerDocu.ClassicWorkflowDocumenter/GraphBuilder.cs`,
`PowerDocu.DesktopFlowDocumenter/GraphBuilder.cs`,
`PowerDocu.AgentDocumenter/GraphBuilder.cs` (two call sites — topic graph +
topic-dataflow graph), `PowerDocu.SolutionDocumenter/SolutionComponentGraphBuilder.cs`,
`PowerDocu.SolutionDocumenter/DataverseGraphBuilder.cs`. 9-line diff total.
Verified: clean rebuild, 0 errors, isolated diff reviewed before committing.

### SharePoint enricher — carried forward, rewritten to be non-destructive

Copied `PowerDocu.SharePointEnricher` across (detector, entities, HTML/MD
builders — all already genuinely standalone). The one real problem: the old
`OutputPatcher.cs` patched `solution-*.html`/`.md`, each referencing
flow/app's own HTML page, and `solution-components.dot/svg/png` **in place**,
via DOM/text surgery keyed to PowerDocu's exact current markup shape (XPath
selectors like `//nav[contains(@class,'sidebar')]/ul[contains(@class,'nav-list')]`)
— fragile by the code's own admission, and exactly the kind of coupling this
whole rebuild exists to avoid.

Replaced with `EnrichmentSummaryWriter.cs`: writes one new file,
`sharepoint-references.json`, at the root of PowerDocu's output folder,
containing every site/list/reference the run found. Nothing PowerDocu wrote
is opened, parsed, or modified. Cross-linking (e.g. "this flow references
list X") moved downstream to the reshell tool, computed from this JSON
rather than injected into PowerDocu's own files. The merged
solution-components diagram (SharePoint nodes drawn into PowerDocu's own
graph) was dropped for this pass — it was the one place the old enricher
overwrote a PowerDocu-owned file, not required for completeness, could come
back later as a *new* file if wanted.

Verified: builds clean (0 errors) against the rest of the new repo.

### Reshell tool — prototyped in Node.js, then ported to C#

**First built as `Shell/build.js`** (Node.js), deliberately, for fast
iteration — same reasoning as the original mockups. Design: a guaranteed-
inclusion floor (pure filesystem enumeration, one node per file, an
exhaustive by-type render rule with no "unrecognized" branch, nav position
defaulting to mirroring the folder path) plus a best-effort enhancement pass
(recognizes the `<Type>Doc <name>` folder convention to build tiered
groups/tabs/TOC/cross-references — upgrades only, never gates inclusion).

Tested hard against real data: built a synthetic output folder from the two
genuinely real examples in `examples\` (Flow: 28 real actions; App: 13 real
screens + real datasources/resources/controls), plus a synthetic
`sharepoint-references.json` to exercise the cross-reference path. Served
over a real HTTP server (the Browser preview pane treats files outside the
project folder as inert `data:` snapshots where hash routing and some
navigation silently no-ops, so a real server was necessary for honest
verification) and drove it end to end via direct JS invocation and browser
automation. Found and fixed several real bugs this way, not by reading the
code:

- **A UTF-8 BOM** that Grynwald.MarkdownGenerator (PowerDocu's Markdown
  writer) prepends to every file broke the `^#` heading-start regex match on
  every file's first line, silently turning every doc's title into a plain
  paragraph with a literal `#` in it.
- **Markdown links to filenames containing `(guid)`** were truncated at the
  first `)` by a naive `[^)]+` capture — real PowerDocu filenames often
  embed exactly that pattern. Fixed by capturing greedily to the *last* `)`
  on the line.
- **Internal cross-links pointed at sibling `.md` files**, which don't exist
  in the rendered site (everything becomes `.html`) — added a rewrite step.
- **A CSS grid layout bug**: adding a `#tabbar` mount point broke the
  `.app` grid's row layout, since the grid was only ever built for
  header+sidebar+main with no accounting for a 4th child. Fixed by giving
  every region an explicit `grid-row`/`grid-column` instead of relying on
  DOM-order auto-placement.
- **A folder-naming assumption**: real folders are `"FlowDoc - Email me..."`
  (space-hyphen-space), not `"FlowDoc Email me..."` (single space) as first
  assumed — the grouping regex silently produced doc labels with a stray
  leading `"- "`. Fixed the regex to tolerate an optional `-` separator.
- **The tab-vs-detail-item split couldn't be based on folder depth**: Flow
  puts per-action files in an `actions\` subfolder, but App puts per-screen
  files flat, alongside its own aggregate files. Fixed by splitting on
  recognized aggregate-file *prefix* (`index-`, `connections-`, etc.)
  instead of depth — this also, as a side effect, correctly swept up real
  per-resource media files (icons/images) that exist in a real `Resources\`
  subfolder nobody had told this tool about in advance, exactly the
  "handles content it's never seen" property the design was meant to have.

**Then ported to C#** (`PowerDocu.Shell`, replacing `Shell/build.js`
entirely) once the user asked whether Step 3 could avoid a Node.js
dependency, given every other step is already a self-contained .NET exe. The
`assets/style.css`/`assets/nav.js` chrome carried over byte-for-byte
unchanged (already generic — reads `window.NAV_TREE`/`SEARCH_INDEX`/`PAGE`
at runtime); `MarkdownRenderer.cs`/`Program.cs` are a close line-for-line
port of the JS logic, using `System.Text.Json` with a camelCase naming
policy to reproduce the exact same `nav-data.js` object shape.

Verified by running both versions against the identical synthetic fixture
and diffing: **identical 141-page output, byte-for-byte identical file
list.** The diff also caught one real regression before it shipped: the
SharePoint tier's CSS/icon type code came out as `"sharepoint"` in the C#
port instead of the short `"sp"` code `nav.js`/`style.css` actually key
their icon/color theming off of (only SharePoint has a short code distinct
from its tier key; every other tier's type equals its key, which is why this
one was easy to collapse by accident). Fixed in `Program.cs`'s `KnownTiers`
table, re-verified.

`Shell/build.js` (the JS prototype) was then deleted from the repo — keeping
two copies of the same logic invites drift, and the C# version has full
parity.

### PipelineUI — three-step launcher

Carried the WPF+PowerShell launcher across from v1, dropping the
"SolutionsDocs" rebrand throughout (exe names, `%APPDATA%`/`%LOCALAPPDATA%`
folder, on-screen labels) via a global text rename — this repo uses
upstream-style naming (`PowerDocu.exe`, not `SolutionsDocs.exe`) everywhere,
consistent with the "no rebrand" decision.

Added `Start-Step3`, mirroring `Start-Step1`/`Start-Step2`'s exact async
process pattern (`DispatcherTimer`-polled `Receive-QueuedLines`, its own step
badge). Only requires Step 1 to have completed — Step 2 is not a
prerequisite, since the reshell tool works from whatever exists on disk.
Auto-chain now runs all three steps in sequence when checked.

Syntax-validated (`[System.Management.Automation.Language.Parser]::ParseFile`)
and XML-validated (XAML), but **not click-tested** — no GUI available in the
environment this was built in. The async pattern was mirrored carefully from
the already-working v1 launcher rather than invented fresh, but a real
click-through on Windows is still worth doing before trusting it fully.

### Public release (superseded within this same session)

Published `v0.1.0` then `v0.2.0` (adding a `Setup.cmd`/`Setup.ps1` that
checked for and installed Node.js + PowerShell 7 + `PnP.PowerShell` via
`winget`) — both verified downloadable anonymously (`curl -sI`, checked for
`200 OK` and the right byte count). Both are now superseded by the
zero-prerequisite pipeline built later in this same session (see next
section) — **a new release with the current code has not yet been cut**,
see HANDOFF.md's "Do this next".

### Removing the remaining prerequisites: C# reshell (above) + PnP.Framework

After shipping `v0.2.0`, the user asked whether Step 3 could avoid Node.js
(see the C# port above) and then whether Step 2's PowerShell 7 +
`PnP.PowerShell` requirement could go the same way. Researched this properly
before writing code (`WebSearch`/`WebFetch`, not assumption) and found two
things worth recording:

1. **PnP.Framework** (a .NET library) exposes the same SharePoint access
   PnP.PowerShell wraps, via `AuthenticationManager.CreateWithInteractiveLogin`
   for MSAL-based interactive sign-in and standard SharePoint CSOM
   (`Microsoft.SharePoint.Client`) for the actual list/field/item fetch — so
   the PowerShell dependency genuinely could be removed.
2. **But an Azure AD app registration cannot be removed, regardless of
   language.** Microsoft retired the shared multi-tenant "PnP Management
   Shell" app in September 2024 for security reasons; every tenant now needs
   its own Entra ID app registration and Client ID, whether the caller is
   `Connect-PnPOnline -Interactive` or `AuthenticationManager.CreateWithInteractiveLogin`.
   This also means **v1's (and this repo's original) PowerShell-based fetcher
   was almost certainly already broken** against any current tenant, since it
   never passed a Client ID either — a pre-existing gap surfaced by this
   research, not introduced by it.

Told the user this clearly before proceeding, since it changes what "no
install scripts needed" can honestly mean (zero *software* installs; one
*tenant configuration* step, unavoidable either way) — confirmed to proceed
with both changes.

**Rewrote `SharePointDataFetcher.cs`** to authenticate via
`AuthenticationManager.CreateWithInteractiveLogin(clientId, redirectUrl)` +
`GetContextAsync(siteUrl)`, then fetch lists via CSOM: non-hidden fields
(with `FieldChoice.Choices` for Choice/MultiChoice columns) and a
CAML-`<RowLimit>`-capped sample of rows via `ListItem.FieldValues` — same
shape as the removed PowerShell script, confirmed field-for-field against
its actual source before rewriting. `Program.cs` now takes a required
`clientId` argument. Removed `Resources\FetchSharePointData.ps1` and updated
`SHAREPOINT-DATA-CONTRACT.md` to describe the new mechanism.

Verified: builds clean against the real `PnP.Framework` 1.21.0 NuGet package
(confirms the CSOM API surface used is real and correctly typed) — **not**
exercised against a live tenant, same limitation this project has had since
Phase 3 was first scoped in v1 (no live tenant has ever been available; see
HANDOFF.md's "Known gaps").

**Wired PipelineUI to both changes**: `Get-ShellExeCandidates` replaces
`Get-NodeExePath`/`Get-ShellScriptCandidates`, resolving `PowerDocu.Shell.exe`
the same packaged-then-dev-build-output way as the other two exes.
`Get-OrPromptClientId` (using `Microsoft.VisualBasic.Interaction.InputBox`)
prompts once for the Entra ID Client ID, only when Step 2 actually runs, and
remembers it in `settings.json` alongside the exe paths. Declining just
skips Step 2 (still auto-chaining into Step 3 if checked) rather than
blocking the run. `Setup.ps1`/`Setup.cmd` were deleted — there is nothing
left to install. `Build-PortablePackage.ps1` now publishes
`PowerDocu.Shell.exe` via `dotnet publish` instead of copying the old
`Shell\` script folder.

**Still open at the end of this session**: cut a new public release with
this code (v0.1.0/v0.2.0 predate all of the above); real end-to-end pipeline
run against a genuine solution zip; real SharePoint fetch against a live
tenant with a real Entra app registration; a real click-through of the WPF
UI on Windows. All captured in HANDOFF.md's "Do this next".
