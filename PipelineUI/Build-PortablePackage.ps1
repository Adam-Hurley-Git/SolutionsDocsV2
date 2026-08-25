<#
    DEV-MACHINE ONLY. Publishes all three pipeline executables (self-contained,
    win-x64) into PipelineUI\bin\, so this whole PipelineUI folder plus the root
    "Atlas PP Doc.vbs" can be zipped and copied to another computer and just
    run - no repo clone, no .NET SDK, nothing else to install there at all.

    Requires the .NET 10 SDK on THIS machine (the one running this script).
    Run it again any time the source changes and you want to refresh the package.

    Nothing else is required on the TARGET machine. Step 2 (SharePoint
    enrichment) still needs a one-time Entra ID app registration - see
    HANDOFF.md - but that is tenant configuration, not software to install.
#>

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$BinDir    = Join-Path $ScriptDir 'bin'

$CoreOut     = Join-Path $BinDir 'PowerDocu'
$EnricherOut = Join-Path $BinDir 'SharePointEnricher'
$ShellOut    = Join-Path $BinDir 'Shell'

Write-Host "Publishing PowerDocu.exe (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot 'PowerDocu.GUI\PowerDocu.GUI.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $CoreOut
if ($LASTEXITCODE -ne 0) { throw 'Publishing PowerDocu.GUI failed.' }

Write-Host "Publishing PowerDocu.SharePointEnricher.exe (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot 'PowerDocu.SharePointEnricher\PowerDocu.SharePointEnricher.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $EnricherOut
if ($LASTEXITCODE -ne 0) { throw 'Publishing PowerDocu.SharePointEnricher failed.' }

Write-Host "Publishing PowerDocu.Shell.exe (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot 'PowerDocu.Shell\PowerDocu.Shell.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o $ShellOut
if ($LASTEXITCODE -ne 0) { throw 'Publishing PowerDocu.Shell failed.' }

Write-Host ""
Write-Host "Done. Packaged into:" -ForegroundColor Green
Write-Host "  $CoreOut"
Write-Host "  $EnricherOut"
Write-Host "  $ShellOut"
Write-Host ""
Write-Host "To move to another computer: copy the repo root's 'Atlas PP Doc.vbs' together with this" -ForegroundColor Green
Write-Host "entire PipelineUI folder (including the bin\ subfolder just created). No repo clone, no" -ForegroundColor Green
Write-Host ".NET SDK, no PowerShell 7, no Node.js - nothing to install on the target machine at all." -ForegroundColor Green
Write-Host "See README.md for the one thing Step 2 still needs: a one-time Entra ID app registration." -ForegroundColor Green
