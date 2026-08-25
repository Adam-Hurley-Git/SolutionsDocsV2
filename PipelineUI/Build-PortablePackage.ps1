<#
    DEV-MACHINE ONLY. Publishes both pipeline executables (self-contained, win-x64)
    into PipelineUI\bin\, and copies the Shell/ reshell tool alongside them, so this
    whole PipelineUI folder plus the root "Atlas PP Doc.vbs" can be zipped and copied
    to another computer and just run - no repo clone, no .NET SDK needed there.

    Requires the .NET 10 SDK on THIS machine (the one running this script).
    Run it again any time the source changes and you want to refresh the package.

    Not bundled by this script, still required on the TARGET machine:
      - pwsh.exe (PowerShell 7+)
      - the PnP.PowerShell module (Install-Module PnP.PowerShell)
        - what the SharePoint enrichment step shells out to for the live fetch
      - Node.js (for Shell/build.js, the reshell step)
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

Write-Host "Copying Shell/ (reshell tool - script files, nothing to compile)..." -ForegroundColor Cyan
if (Test-Path $ShellOut) { Remove-Item -Recurse -Force $ShellOut }
Copy-Item (Join-Path $RepoRoot 'Shell') $ShellOut -Recurse

Write-Host ""
Write-Host "Done. Packaged into:" -ForegroundColor Green
Write-Host "  $CoreOut"
Write-Host "  $EnricherOut"
Write-Host "  $ShellOut"
Write-Host ""
Write-Host "To move to another computer: copy the repo root's 'Atlas PP Doc.vbs' together with this" -ForegroundColor Green
Write-Host "entire PipelineUI folder (including the bin\ subfolder just created). No repo clone or" -ForegroundColor Green
Write-Host ".NET SDK is needed on the target machine - only pwsh.exe + PnP.PowerShell (SharePoint" -ForegroundColor Green
Write-Host "step) and Node.js (reshell step). See README.md." -ForegroundColor Green
