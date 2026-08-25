# Atlas PP Doc - pipeline launcher

A small desktop UI that runs the full documentation pipeline: PowerDocu (core
documentation), then optionally the SharePoint enricher, then optionally the
reshell tool that builds a custom navigable/searchable view - with live
status for each step, an output-folder shortcut, and a log you can safely
paste back to Claude if something goes wrong.

No build step for this UI itself. It's a PowerShell + WPF script - just run it.

## Running it

Double-click **`Atlas PP Doc.vbs`** at the repo root. That's it - only this
window appears; nothing else pops up, not even a console flash (see "What
you'll see" below for the one exception).

If it's the first time on this machine and `PowerDocu.exe` /
`PowerDocu.SharePointEnricher.exe` can't be found automatically, you'll be
asked to locate them once - after that they're remembered.

## Prerequisites

**On the machine that builds the package** (this dev machine, with the repo
and .NET 10 SDK): nothing extra beyond what's already needed to build
PowerDocu.

**On the machine that just runs the packaged pipeline** (e.g. the other
computer with real tenant/SharePoint access):

- For the SharePoint enrichment step's live fetch - [PowerShell 7+](https://aka.ms/powershell)
  (`pwsh.exe`) and the `PnP.PowerShell` module: `Install-Module PnP.PowerShell -Scope CurrentUser`
- For the custom-view (reshell) step - [Node.js](https://nodejs.org/) (any
  reasonably recent LTS). `Shell/build.js` is plain script files, nothing to
  compile - Node just needs to exist on PATH.

Nothing else - no repo clone, no .NET SDK, no Visual Studio. This is
intentional: `Build-PortablePackage.ps1` publishes both exes as
self-contained, so the `.NET` runtime ships inside `PipelineUI\bin\` already.

## Building/refreshing the portable package (dev machine only)

```powershell
powershell -ExecutionPolicy Bypass -File Build-PortablePackage.ps1
```

Run from inside `PipelineUI\`. This publishes both exes (self-contained,
win-x64) into `PipelineUI\bin\PowerDocu\` and `PipelineUI\bin\SharePointEnricher\`,
and copies `Shell/` (no build needed - it's script files) into
`PipelineUI\bin\Shell\`. Re-run it any time source changes and you want the
package refreshed.

## Moving to another computer

Copy the repo root's `Atlas PP Doc.vbs` together with the entire `PipelineUI`
folder (including `bin\`, once built). Zip it, copy it over, unzip, double
click `Atlas PP Doc.vbs`. That's the whole setup - see Prerequisites above for
the two things (`pwsh` + `PnP.PowerShell`, and Node.js) still needed on that
machine, and only if you actually use those steps.

## What you'll see

Only this app's own window, for the entire run - the underlying
`PowerDocu.exe` never shows its own window when launched this way (it detects
it's being run with arguments and skips its GUI), and the SharePoint
enricher's console window is suppressed the same way. The reshell step has no
window of its own either - it's a headless script.

**One expected exception**: when the SharePoint enrichment step reaches its
live data fetch, it needs you to sign in - a genuine Microsoft sign-in
window/browser popup will appear once. That's expected; it's how you actually
authenticate to the tenant, not a bug or a stray window.

## The three steps

1. **PowerDocu documentation** - runs the unedited-upstream (plus one small
   DOT-export patch) core generator against your solution zip.
2. **SharePoint enrichment** - detects SharePoint references in the flows/apps
   just documented, and writes its own site/list/column data plus
   `sharepoint-references.json` into the same output folder, as new files
   only. Never opens a file Step 1 wrote.
3. **Custom view (reshell)** - reads Steps 1 and 2's output (read-only) and
   builds a navigable, searchable HTML view at `<OutputFolder>\Shell\index.html`.
   Only needs Step 1 to have run; Step 2 is not required, just adds SharePoint
   cross-references to the result if it did run.

Each step can be re-run independently once Step 1 has completed for the
current inputs - you don't have to redo an earlier step to retry a later one.

## The two log files

Every run writes to `%LOCALAPPDATA%\PowerDocu\PipelineUI\logs\run-<timestamp>\`:

- **`raw-local.log`** - full detail, mirrors the on-screen live log. **May
  contain real tenant data** (site URLs, list/column names). Local
  troubleshooting only - never share this file.
- **`summary-shareable.log`** - structured status only (which step, pass/
  fail/partial, generic counts, redacted error text). This is what "Copy
  shareable summary" copies to your clipboard - safe to paste into a chat
  with Claude if something needs debugging.
