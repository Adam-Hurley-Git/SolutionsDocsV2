<#
    Solutions Docs v2 - one-time setup.

    Checks for and installs the prerequisites needed by all three pipeline
    steps, so "Atlas PP Doc.vbs" works right away with nothing else to set up
    first. Safe to re-run any time - every check is skip-if-already-present.

      Step 1 (PowerDocu documentation)   - needs nothing beyond this zip.
      Step 2 (SharePoint enrichment)     - needs PowerShell 7 + the
                                            PnP.PowerShell module.
      Step 3 (custom view / reshell)     - needs Node.js.

    Installs via winget where possible. If winget isn't on this machine,
    prints a manual download link instead of failing silently.
#>

$ErrorActionPreference = 'Stop'
$script:results = @()

function Write-Status {
    param([string]$Label, [string]$State, [string]$Detail = '')
    $color = switch ($State) {
        'OK'        { 'Green' }
        'Installed' { 'Green' }
        'Skipped'   { 'Yellow' }
        'Failed'    { 'Red' }
        default     { 'Gray' }
    }
    Write-Host ("  [{0}] {1}" -f $State.PadRight(9), $Label) -ForegroundColor $color
    if ($Detail) { Write-Host "           $Detail" -ForegroundColor DarkGray }
    $script:results += [PSCustomObject]@{ Label = $Label; State = $State; Detail = $Detail }
}

function Update-SessionPath {
    # winget updates the registry PATH, not the copy this already-running
    # process inherited at launch - without this, a just-installed command
    # stays invisible to the rest of this script until the window is reopened.
    $machine = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = @($machine, $user) -join ';'
}

Write-Host ""
Write-Host "Solutions Docs v2 - checking prerequisites for all three steps" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host ""

$hasWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
if (-not $hasWinget) {
    Write-Host "winget (Windows Package Manager) was not found on this machine - anything" -ForegroundColor Yellow
    Write-Host "missing below will need to be installed manually using the link shown." -ForegroundColor Yellow
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Step 1 - no prerequisites at all
# ---------------------------------------------------------------------------
Write-Status 'Step 1 - PowerDocu documentation' 'OK' 'No prerequisites - ready to use.'

# ---------------------------------------------------------------------------
# Step 3 - Node.js
# ---------------------------------------------------------------------------
if (Get-Command node -ErrorAction SilentlyContinue) {
    Write-Status 'Node.js (Step 3 - custom view)' 'OK' (node --version)
} elseif ($hasWinget) {
    Write-Status 'Node.js (Step 3 - custom view)' 'Installing' 'winget install OpenJS.NodeJS.LTS ... (Windows may prompt for permission - that is normal)'
    winget install --id OpenJS.NodeJS.LTS -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
    Update-SessionPath
    if (Get-Command node -ErrorAction SilentlyContinue) {
        Write-Status 'Node.js (Step 3 - custom view)' 'Installed' (node --version)
    } else {
        Write-Status 'Node.js (Step 3 - custom view)' 'Failed' 'Installed but not detected yet - close this window, reopen it, and run Setup again.'
    }
} else {
    Write-Status 'Node.js (Step 3 - custom view)' 'Skipped' 'Install manually: https://nodejs.org/ (LTS), then re-run Setup.'
}

# ---------------------------------------------------------------------------
# Step 2 - PowerShell 7
# ---------------------------------------------------------------------------
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) {
    $ver = & pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    Write-Status 'PowerShell 7 (Step 2 - SharePoint enrichment)' 'OK' $ver
} elseif ($hasWinget) {
    Write-Status 'PowerShell 7 (Step 2 - SharePoint enrichment)' 'Installing' 'winget install Microsoft.PowerShell ... (Windows may prompt for permission - that is normal)'
    winget install --id Microsoft.PowerShell -e --silent --accept-package-agreements --accept-source-agreements | Out-Null
    Update-SessionPath
    $pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwshCmd) {
        $ver = & pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
        Write-Status 'PowerShell 7 (Step 2 - SharePoint enrichment)' 'Installed' $ver
    } else {
        Write-Status 'PowerShell 7 (Step 2 - SharePoint enrichment)' 'Failed' 'Installed but not detected yet - close this window, reopen it, and run Setup again.'
    }
} else {
    Write-Status 'PowerShell 7 (Step 2 - SharePoint enrichment)' 'Skipped' 'Install manually: https://aka.ms/powershell, then re-run Setup.'
}

# ---------------------------------------------------------------------------
# Step 2 - PnP.PowerShell module (needs PowerShell 7 present first)
# ---------------------------------------------------------------------------
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if (-not $pwshCmd) {
    Write-Status 'PnP.PowerShell module (Step 2)' 'Skipped' 'Needs PowerShell 7 first (see above) - re-run Setup once that is installed.'
} else {
    $hasPnP = & pwsh -NoProfile -Command "if (Get-Module -ListAvailable -Name PnP.PowerShell) { 'yes' } else { 'no' }"
    if ($hasPnP -eq 'yes') {
        Write-Status 'PnP.PowerShell module (Step 2)' 'OK'
    } else {
        Write-Status 'PnP.PowerShell module (Step 2)' 'Installing' 'Install-Module PnP.PowerShell -Scope CurrentUser ...'
        & pwsh -NoProfile -Command "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue; Install-Module PnP.PowerShell -Scope CurrentUser -Force -AllowClobber" 2>&1 | Out-Null
        $hasPnP = & pwsh -NoProfile -Command "if (Get-Module -ListAvailable -Name PnP.PowerShell) { 'yes' } else { 'no' }"
        if ($hasPnP -eq 'yes') {
            Write-Status 'PnP.PowerShell module (Step 2)' 'Installed'
        } else {
            Write-Status 'PnP.PowerShell module (Step 2)' 'Failed' 'Run manually in a pwsh window: Install-Module PnP.PowerShell -Scope CurrentUser'
        }
    }
}

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Cyan
$failed = $script:results | Where-Object { $_.State -eq 'Failed' -or $_.State -eq 'Skipped' }
if ($failed.Count -eq 0) {
    Write-Host "All set - every step is ready. Double-click 'Atlas PP Doc.vbs' to run the pipeline." -ForegroundColor Green
} else {
    Write-Host "Step 1 (core documentation) is always ready regardless of the above." -ForegroundColor Green
    Write-Host "Anything marked Failed or Skipped above only affects that one step -" -ForegroundColor Yellow
    Write-Host "the others will still work. Fix it and re-run Setup any time." -ForegroundColor Yellow
}
Write-Host ""
