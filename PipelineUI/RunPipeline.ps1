<#
    Atlas PP Doc - pipeline launcher.

    Three steps, each optional/chainable, each safe to re-run independently:
      1. PowerDocu.exe                     - core documentation (unedited PowerDocu + the
                                              one DOT-export patch)
      2. PowerDocu.SharePointEnricher.exe   - SharePoint enrichment; writes its own files
                                              only, never touches Step 1's output
      3. Shell/build.js (via node)          - reshell into the custom navigable/searchable
                                              view; reads Steps 1+2's output read-only,
                                              writes to <OutputFolder>\Shell only

    Shows live status for each and writes two logs per run:
      - raw-local.log         full detail, may contain real tenant data, never share
      - summary-shareable.log structured + redacted, safe to paste back to Claude

    No build step for this UI itself - just run it (via the "Atlas PP Doc.vbs"
    launcher one folder up, or directly with Windows PowerShell).
#>

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Xaml

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir

$AppDataDir   = Join-Path $env:APPDATA 'PowerDocu\PipelineUI'
$SettingsPath = Join-Path $AppDataDir 'settings.json'
$LogsRoot     = Join-Path $env:LOCALAPPDATA 'PowerDocu\PipelineUI\logs'
New-Item -ItemType Directory -Force -Path $AppDataDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogsRoot | Out-Null

$BadgeColors = @{
    Pending = @{ Bg = '#EDEDEC'; Fg = '#6B6B68' }
    Running = @{ Bg = '#E6F1FB'; Fg = '#185FA5' }
    Success = @{ Bg = '#EAF3DE'; Fg = '#3B6D11' }
    Partial = @{ Bg = '#FAEEDA'; Fg = '#854F0B' }
    Failed  = @{ Bg = '#FCEBEB'; Fg = '#A32D2D' }
    Skipped = @{ Bg = '#EDEDEC'; Fg = '#6B6B68' }
}

# ---------------------------------------------------------------------------
# Settings ( %APPDATA%\PowerDocu\PipelineUI\settings.json )
# ---------------------------------------------------------------------------

function Get-Settings {
    if (Test-Path $SettingsPath) {
        try {
            $loaded = Get-Content -Path $SettingsPath -Raw | ConvertFrom-Json
            if ($loaded) { return $loaded }
        } catch { }
    }
    return [PSCustomObject]@{
        CoreExePath = $null
        EnricherExePath  = $null
        ShellExePath     = $null
        ClientId         = $null
        LastZipPath      = $null
        LastOutputFolder = $null
    }
}

function Save-Settings {
    param($Settings)
    try { $Settings | ConvertTo-Json | Set-Content -Path $SettingsPath -Encoding UTF8 } catch { }
}

# ---------------------------------------------------------------------------
# Exe discovery - packaged (PipelineUI\bin) location first, then dev-repo build output
# ---------------------------------------------------------------------------

function Get-CoreExeCandidates {
    @(
        (Join-Path $ScriptDir 'bin\PowerDocu\PowerDocu.exe')
        (Join-Path $RepoRoot 'PowerDocu.GUI\bin\Release\net10.0-windows\PowerDocu.exe')
        (Join-Path $RepoRoot 'PowerDocu.GUI\bin\Debug\net10.0-windows\PowerDocu.exe')
    )
}

function Get-EnricherExeCandidates {
    @(
        (Join-Path $ScriptDir 'bin\SharePointEnricher\PowerDocu.SharePointEnricher.exe')
        (Join-Path $RepoRoot 'PowerDocu.SharePointEnricher\bin\Release\net10.0\PowerDocu.SharePointEnricher.exe')
        (Join-Path $RepoRoot 'PowerDocu.SharePointEnricher\bin\Debug\net10.0\PowerDocu.SharePointEnricher.exe')
    )
}

function Resolve-ExePath {
    param([string]$SettingValue, [string[]]$Candidates)
    if ($SettingValue -and (Test-Path $SettingValue)) { return $SettingValue }
    foreach ($c in $Candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Get-OrPromptExePath {
    param([string]$SettingKeyName, [string[]]$Candidates, [string]$DisplayName)

    $current = $script:State.Settings.$SettingKeyName
    $resolved = Resolve-ExePath -SettingValue $current -Candidates $Candidates
    if ($resolved) { return $resolved }

    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = "Locate $DisplayName"
    $dlg.Filter = 'Executable (*.exe)|*.exe'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:State.Settings.$SettingKeyName = $dlg.FileName
        Save-Settings $script:State.Settings
        return $dlg.FileName
    }
    return $null
}

# ---------------------------------------------------------------------------
# SharePoint sign-in Client ID - not a secret (it's a public app identifier),
# so a plain remembered setting is fine, same as the exe paths above. Needed
# because Microsoft retired the shared multi-tenant PnP app in Sept 2024 -
# every tenant now needs its own Entra ID app registration. See HANDOFF.md
# for the one-time registration steps. Only prompted when Step 2 actually
# runs, not at startup, since plenty of runs never touch SharePoint.
# ---------------------------------------------------------------------------

function Get-OrPromptClientId {
    if ($script:State.Settings.ClientId) { return $script:State.Settings.ClientId }

    Add-Type -AssemblyName Microsoft.VisualBasic
    $input = [Microsoft.VisualBasic.Interaction]::InputBox(
        "SharePoint enrichment needs the Application (client) ID from a one-time Entra ID app registration - see HANDOFF.md for the exact steps.`n`nPaste it here (leave blank to skip SharePoint enrichment this run):",
        'Atlas PP Doc - SharePoint sign-in',
        ''
    )
    if ([string]::IsNullOrWhiteSpace($input)) { return $null }

    $script:State.Settings.ClientId = $input.Trim()
    Save-Settings $script:State.Settings
    return $script:State.Settings.ClientId
}

# ---------------------------------------------------------------------------
# Logging - two files per run: raw (local only) and sanitized summary (shareable)
# ---------------------------------------------------------------------------

function New-LogSession {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dir = Join-Path $LogsRoot "run-$stamp"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $raw = Join-Path $dir 'raw-local.log'
    $summary = Join-Path $dir 'summary-shareable.log'
    Set-Content -Path $raw -Value "LOCAL ONLY -- may contain real tenant data (site URLs, list/column names, sample rows). Do not share this file. Run started $stamp." -Encoding UTF8
    Set-Content -Path $summary -Value "Atlas PP Doc run summary -- safe to share. Run started $stamp." -Encoding UTF8
    [PSCustomObject]@{ Dir = $dir; RawPath = $raw; SummaryPath = $summary }
}

function Protect-SensitiveText {
    # Verified against a real PnP/pwsh error (SharePointDataFetcher's stderr passthrough,
    # which is real PowerShell 7 Write-Error output - ANSI color codes included even when
    # redirected) that this needs an ANSI-stripping pass first, or escape codes end up as
    # literal garbage in the sanitized log instead of being cleaned away.
    param([string]$Text)
    if (-not $Text) { return $Text }
    $t = $Text
    # [char]27, not the `e escape token: `e silently degrades to a literal "e" in
    # Windows PowerShell 5.1 (confirmed - [int][char]"`e" is 101 there, not 27), and
    # this app's launcher deliberately runs under plain powershell.exe (5.1), not pwsh.
    $esc = [char]27
    $t = [regex]::Replace($t, "$esc\[[0-9;]*[a-zA-Z]", '')
    $t = [regex]::Replace($t, "https?://[^\s'`"]+", '[URL]')
    $t = [regex]::Replace($t, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '[GUID]')
    $t = [regex]::Replace($t, '\S+@\S+\.\S+', '[EMAIL]')
    $t = [regex]::Replace($t, "'[^']*'", "'[REDACTED]'")
    $t = [regex]::Replace($t, '"[^"]*"', '"[REDACTED]"')
    $t = [regex]::Replace($t, '[A-Za-z]:\\\S+', '[PATH]')
    return $t
}

function Write-RawLine {
    # Only ever called from a DispatcherTimer.Tick handler (Start-Step1/Start-Step2),
    # which already runs on the UI thread - no cross-thread marshaling needed here.
    param($State, [string]$Line)
    try { Add-Content -Path $State.LogRawPath -Value $Line -Encoding UTF8 } catch { }
    $State.Controls.LiveLogBox.AppendText("$Line`r`n")
    $State.Controls.LiveLogBox.ScrollToEnd()
}

function Receive-QueuedLines {
    # Drains events already queued by Register-ObjectEvent (registered WITHOUT -Action).
    # Deliberately not using -Action's automatic background dispatch: verified empirically
    # that it silently drops the majority of lines under any real output volume (confirmed
    # against a real PowerDocu run - only 6 of 44 real lines arrived that way). Explicit
    # Get-Event/Remove-Event draining, called from a UI-thread DispatcherTimer tick, is the
    # combination that reliably captures every line while staying off any background thread
    # that PowerShell has no runspace for (also verified empirically).
    param([string]$SourceId, [scriptblock]$OnLine)
    $queued = Get-Event -SourceIdentifier $SourceId -ErrorAction SilentlyContinue
    foreach ($e in $queued) {
        $line = $e.SourceEventArgs.Data
        if ($null -ne $line) { & $OnLine $line }
        Remove-Event -EventIdentifier $e.EventIdentifier -ErrorAction SilentlyContinue
    }
}

function Write-SummaryLine {
    param($State, [string]$Line)
    $stamped = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Line
    try { Add-Content -Path $State.LogSummaryPath -Value $stamped -Encoding UTF8 } catch { }
    [void]$State.SummaryLines.Add($stamped)
}

function Set-StepBadge {
    param($BorderControl, $TextControl, [string]$Status)
    $c = $BadgeColors[$Status]
    $BorderControl.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($c.Bg))
    $TextControl.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($c.Fg))
    $TextControl.Text = $Status
}

# ---------------------------------------------------------------------------
# Step 1 - PowerDocu.exe
#   Exit code is never trustworthy (the CLI catches exceptions and
#   returns normally) - success is only the "Documentation completed..." line.
#
#   Everything here operates on $script:State only (never a function-local
#   parameter) - verified empirically that a DispatcherTimer.Tick handler does
#   NOT reliably close over a function's local variables once that function
#   has returned (confirmed: both the local var itself and calling .Stop() on
#   a function-local timer failed silently). $script:-scoped state and
#   top-level function calls both work correctly from inside Tick handlers;
#   only function-local closures do not. See also Receive-QueuedLines' notes
#   on why polling replaces Register-ObjectEvent's -Action dispatch.
# ---------------------------------------------------------------------------

function Start-Step1 {
    if (-not $script:State.CoreExePath -or -not (Test-Path $script:State.CoreExePath)) {
        Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 1: PowerDocu documentation - failed (PowerDocu.exe not located)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        return
    }

    Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Running'
    Write-SummaryLine $script:State 'Step 1: PowerDocu documentation - started'
    Write-RawLine $script:State '=== Step 1: PowerDocu documentation ==='

    $script:State.Step1SuccessSeen = $false
    $script:State.Step1StderrSeen = $false
    $script:State.Step1FirstError = $null
    $script:State.Step1ExitPending = $false

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:State.CoreExePath
    $psi.Arguments = '-q "{0}" -w -m -h -f -o "{1}"' -f $script:State.ZipPath, $script:State.OutputFolder
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $runId = [guid]::NewGuid().ToString('N')
    $outId = "S1Out-$runId"
    $errId = "S1Err-$runId"
    Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -SourceIdentifier $outId | Out-Null
    Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -SourceIdentifier $errId | Out-Null

    try {
        $proc.Start() | Out-Null
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
    } catch {
        Unregister-Event -SourceIdentifier $outId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errId -ErrorAction SilentlyContinue
        Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 1: PowerDocu documentation - failed (could not start PowerDocu.exe)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        return
    }

    $script:State.Step1Proc = $proc
    $script:State.Step1OutId = $outId
    $script:State.Step1ErrId = $errId

    $script:Step1Timer = New-Object System.Windows.Threading.DispatcherTimer
    $script:Step1Timer.Interval = [TimeSpan]::FromMilliseconds(200)
    $script:Step1Timer.Add_Tick({
        Receive-QueuedLines -SourceId $script:State.Step1OutId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line $line
            if ($line -match '^Documentation completed for .+\. Total time: [\d.]+ seconds\.$') {
                $script:State.Step1SuccessSeen = $true
            }
        }
        Receive-QueuedLines -SourceId $script:State.Step1ErrId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line "[stderr] $line"
            $script:State.Step1StderrSeen = $true
            if (-not $script:State.Step1FirstError -and $line -match '^([\w.]+Exception)') {
                $script:State.Step1FirstError = $Matches[1]
            }
        }

        if (-not $script:State.Step1Proc.HasExited) { return }
        if (-not $script:State.Step1ExitPending) {
            # First tick after exit - give one more interval for trailing buffered
            # output to arrive before treating the run as fully drained.
            $script:State.Step1ExitPending = $true
            return
        }

        $script:Step1Timer.Stop()
        Unregister-Event -SourceIdentifier $script:State.Step1OutId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $script:State.Step1ErrId -ErrorAction SilentlyContinue

        # The success marker is authoritative: PowerDocu.exe can print non-fatal
        # stderr warnings (e.g. a known pre-existing gvplugin_pango.dll Graphviz
        # plugin warning) on an otherwise fully successful run, so stderr content
        # alone must never flip this to Failed - only the marker's absence does.
        if ($script:State.Step1SuccessSeen) {
            Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Success'
            if ($script:State.Step1StderrSeen) {
                Write-SummaryLine $script:State 'Step 1: PowerDocu documentation - success (with a non-fatal warning - see local log)'
            } else {
                Write-SummaryLine $script:State 'Step 1: PowerDocu documentation - success'
            }
            $script:State.Step1CompletedForCurrentInputs = $true
            $script:State.Controls.OpenOutputButton.IsEnabled = $true
            $script:State.Controls.RunSpButton.IsEnabled = $true
            $script:State.Controls.RunShellButton.IsEnabled = $true
            if ($script:State.Controls.AutoChainCheckBox.IsChecked) {
                Start-Step2
            } else {
                Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Skipped'
                $script:State.Controls.RunPipelineButton.IsEnabled = $true
                $script:State.Controls.CopySummaryButton.IsEnabled = $true
            }
        } else {
            Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Failed'
            $errLabel = if ($script:State.Step1FirstError) { Protect-SensitiveText $script:State.Step1FirstError } else { 'see local log' }
            Write-SummaryLine $script:State "Step 1: PowerDocu documentation - failed ($errLabel)"
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Skipped'
            Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Skipped'
            $script:State.Controls.RunPipelineButton.IsEnabled = $true
            $script:State.Controls.OpenOutputButton.IsEnabled = $true
            $script:State.Controls.CopySummaryButton.IsEnabled = $true
        }
    })
    $script:Step1Timer.Start()
}

# ---------------------------------------------------------------------------
# Step 2 - PowerDocu.SharePointEnricher.exe
#   Exit code 0 covers full success AND every graceful degraded/no-op path -
#   the real state comes from which known terminal line appeared in stdout.
#   Per-reference detail lines (real site URL + list id) are shown live only,
#   never written to the sanitized summary log.
# ---------------------------------------------------------------------------

function Start-Step2 {
    if (-not $script:State.EnricherExePath -or -not (Test-Path $script:State.EnricherExePath)) {
        Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - failed (PowerDocu.SharePointEnricher.exe not located)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        $script:State.Controls.RunSpButton.IsEnabled = $true
        return
    }

    $clientId = Get-OrPromptClientId
    if (-not $clientId) {
        Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Skipped'
        Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - skipped (no Client ID entered - see HANDOFF.md for how to register one)'
        if ($script:State.Controls.AutoChainCheckBox.IsChecked) {
            Start-Step3
        } else {
            $script:State.Controls.RunPipelineButton.IsEnabled = $true
            $script:State.Controls.RunSpButton.IsEnabled = $true
            $script:State.Controls.RunShellButton.IsEnabled = $true
        }
        return
    }

    Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Running'
    Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - started'
    Write-RawLine $script:State '=== Step 2: SharePoint enrichment ==='
    $script:State.Controls.RunPipelineButton.IsEnabled = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false

    $script:State.Step2FoundCount = $null
    $script:State.Step2Terminal = $null
    $script:State.Step2StderrLines = New-Object System.Collections.ArrayList
    $script:State.Step2ExitPending = $false

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:State.EnricherExePath
    $psi.Arguments = '"{0}" "{1}" "{2}"' -f $script:State.ZipPath, $script:State.OutputFolder, $clientId
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $runId = [guid]::NewGuid().ToString('N')
    $outId = "S2Out-$runId"
    $errId = "S2Err-$runId"
    Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -SourceIdentifier $outId | Out-Null
    Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -SourceIdentifier $errId | Out-Null

    try {
        $proc.Start() | Out-Null
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
    } catch {
        Unregister-Event -SourceIdentifier $outId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errId -ErrorAction SilentlyContinue
        Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - failed (could not start PowerDocu.SharePointEnricher.exe)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        $script:State.Controls.RunSpButton.IsEnabled = $true
        return
    }

    $script:State.Step2Proc = $proc
    $script:State.Step2OutId = $outId
    $script:State.Step2ErrId = $errId

    $script:Step2Timer = New-Object System.Windows.Threading.DispatcherTimer
    $script:Step2Timer.Interval = [TimeSpan]::FromMilliseconds(200)
    $script:Step2Timer.Add_Tick({
        Receive-QueuedLines -SourceId $script:State.Step2OutId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line $line
            if ($line -match '^Found (\d+) SharePoint reference\(s\):') {
                $script:State.Step2FoundCount = [int]$Matches[1]
            }
            elseif ($line -eq 'No SharePoint references found in this solution. Nothing to enrich.') {
                $script:State.Step2Terminal = 'NoReferences'
            }
            elseif ($line -eq 'No references had a confirmed list ID to fetch (all were best-effort legacy-format guesses). Stopping before the live fetch step.') {
                $script:State.Step2Terminal = 'NoConfidentReferences'
            }
            elseif ($line -eq 'No SharePoint data was fetched (see errors above). Stopping before page generation.') {
                $script:State.Step2Terminal = 'FetchFailed'
            }
            elseif ($line -eq 'Done.') {
                $script:State.Step2Terminal = 'FullSuccess'
            }
        }
        Receive-QueuedLines -SourceId $script:State.Step2ErrId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line "[stderr] $line"
            [void]$script:State.Step2StderrLines.Add($line)
        }

        if (-not $script:State.Step2Proc.HasExited) { return }
        if (-not $script:State.Step2ExitPending) {
            $script:State.Step2ExitPending = $true
            return
        }

        $script:Step2Timer.Stop()
        Unregister-Event -SourceIdentifier $script:State.Step2OutId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $script:State.Step2ErrId -ErrorAction SilentlyContinue

        $exitCode = $script:State.Step2Proc.ExitCode
        $countText = if ($null -ne $script:State.Step2FoundCount) { " ($($script:State.Step2FoundCount) reference(s) detected)" } else { '' }

        if ($exitCode -eq 1) {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Failed'
            Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - failed (bad usage or missing input - see local log)'
        }
        elseif ($script:State.Step2Terminal -eq 'FullSuccess') {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Success'
            Write-SummaryLine $script:State "Step 2: SharePoint enrichment - success$countText, own pages + sharepoint-references.json written (nothing from Step 1 touched)"
        }
        elseif ($script:State.Step2Terminal -eq 'NoReferences') {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Success'
            Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - success (0 references found, nothing to enrich)'
        }
        elseif ($script:State.Step2Terminal -eq 'NoConfidentReferences') {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Partial'
            Write-SummaryLine $script:State "Step 2: SharePoint enrichment - partial$countText, only low-confidence legacy-format matches, stopped before fetch"
        }
        elseif ($script:State.Step2Terminal -eq 'FetchFailed') {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Partial'
            Write-SummaryLine $script:State "Step 2: SharePoint enrichment - partial$countText, live fetch did not complete (check sign-in/network on this machine)"
        }
        else {
            Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Failed'
            Write-SummaryLine $script:State 'Step 2: SharePoint enrichment - failed (exited without a recognized completion message - see local log)'
        }

        if ($script:State.Step2StderrLines.Count -gt 0) {
            # The most diagnostically useful line (e.g. "Failed to connect to '[URL]':
            # <.NET exception message>") is often not the first stderr line - PowerShell's
            # multi-line Write-Error formatting puts source/context lines first - so redact
            # and include a capped run of lines rather than just the first one.
            $capped = $script:State.Step2StderrLines | Select-Object -First 6
            $redactedJoined = ($capped | ForEach-Object { Protect-SensitiveText ([string]$_) }) -join ' | '
            Write-SummaryLine $script:State "Step 2 warning: $redactedJoined"
        }

        $script:State.Controls.RunShellButton.IsEnabled = $true
        if ($script:State.Controls.AutoChainCheckBox.IsChecked) {
            Start-Step3
        } else {
            $script:State.Controls.RunPipelineButton.IsEnabled = $true
            $script:State.Controls.RunSpButton.IsEnabled = $true
            $script:State.Controls.OpenOutputButton.IsEnabled = $true
            $script:State.Controls.CopySummaryButton.IsEnabled = $true
        }
    })
    $script:Step2Timer.Start()
}

# ---------------------------------------------------------------------------
# Step 3 - PowerDocu.Shell.exe (reshell tool)
#   Read-only on Step 1/2's output folder; writes to <OutputFolder>\Shell only.
#   Never blocks on Step 2 having run - it works from whatever Step 1 (and,
#   if present, Step 2) actually wrote, nothing more is required. Self-
#   contained exe - nothing to install to run it, unlike the old Node.js
#   version this replaced.
# ---------------------------------------------------------------------------

function Get-ShellExeCandidates {
    @(
        (Join-Path $ScriptDir 'bin\Shell\PowerDocu.Shell.exe')
        (Join-Path $RepoRoot 'PowerDocu.Shell\bin\Release\net10.0\PowerDocu.Shell.exe')
        (Join-Path $RepoRoot 'PowerDocu.Shell\bin\Debug\net10.0\PowerDocu.Shell.exe')
    )
}

function Start-Step3 {
    if (-not $script:State.ShellExePath -or -not (Test-Path $script:State.ShellExePath)) {
        Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 3: Custom view - failed (PowerDocu.Shell.exe not located)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        $script:State.Controls.RunSpButton.IsEnabled = $true
        $script:State.Controls.RunShellButton.IsEnabled = $true
        return
    }

    Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Running'
    Write-SummaryLine $script:State 'Step 3: Custom view - started'
    Write-RawLine $script:State '=== Step 3: Custom view (PowerDocu.Shell.exe) ==='
    $script:State.Controls.RunPipelineButton.IsEnabled = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false
    $script:State.Controls.RunShellButton.IsEnabled = $false

    $script:State.Step3StderrLines = New-Object System.Collections.ArrayList
    $script:State.Step3ExitPending = $false

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:State.ShellExePath
    $psi.Arguments = '"{0}"' -f $script:State.OutputFolder
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $runId = [guid]::NewGuid().ToString('N')
    $outId = "S3Out-$runId"
    $errId = "S3Err-$runId"
    Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -SourceIdentifier $outId | Out-Null
    Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -SourceIdentifier $errId | Out-Null

    try {
        $proc.Start() | Out-Null
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
    } catch {
        Unregister-Event -SourceIdentifier $outId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errId -ErrorAction SilentlyContinue
        Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Failed'
        Write-SummaryLine $script:State 'Step 3: Custom view - failed (could not start PowerDocu.Shell.exe)'
        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        $script:State.Controls.RunSpButton.IsEnabled = $true
        $script:State.Controls.RunShellButton.IsEnabled = $true
        return
    }

    $script:State.Step3Proc = $proc
    $script:State.Step3OutId = $outId
    $script:State.Step3ErrId = $errId

    $script:Step3Timer = New-Object System.Windows.Threading.DispatcherTimer
    $script:Step3Timer.Interval = [TimeSpan]::FromMilliseconds(200)
    $script:Step3Timer.Add_Tick({
        Receive-QueuedLines -SourceId $script:State.Step3OutId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line $line
        }
        Receive-QueuedLines -SourceId $script:State.Step3ErrId -OnLine {
            param($line)
            Write-RawLine -State $script:State -Line "[stderr] $line"
            [void]$script:State.Step3StderrLines.Add($line)
        }

        if (-not $script:State.Step3Proc.HasExited) { return }
        if (-not $script:State.Step3ExitPending) {
            $script:State.Step3ExitPending = $true
            return
        }

        $script:Step3Timer.Stop()
        Unregister-Event -SourceIdentifier $script:State.Step3OutId -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $script:State.Step3ErrId -ErrorAction SilentlyContinue

        if ($script:State.Step3Proc.ExitCode -eq 0) {
            Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Success'
            Write-SummaryLine $script:State 'Step 3: Custom view - success (see OutputFolder\Shell\index.html)'
        } else {
            Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Failed'
            Write-SummaryLine $script:State 'Step 3: Custom view - failed (see local log)'
        }

        $script:State.Controls.RunPipelineButton.IsEnabled = $true
        $script:State.Controls.RunSpButton.IsEnabled = $true
        $script:State.Controls.RunShellButton.IsEnabled = $true
        $script:State.Controls.OpenOutputButton.IsEnabled = $true
        $script:State.Controls.CopySummaryButton.IsEnabled = $true
    })
    $script:Step3Timer.Start()
}

# ---------------------------------------------------------------------------
# Window bootstrap
# ---------------------------------------------------------------------------

[xml]$xamlXml = Get-Content -Path (Join-Path $ScriptDir 'MainWindow.xaml') -Raw
$xamlReader = New-Object System.Xml.XmlNodeReader $xamlXml
$window = [System.Windows.Markup.XamlReader]::Load($xamlReader)

$controlNames = @(
    'ZipPathBox', 'BrowseZipButton', 'OutputFolderBox', 'ChooseOutputButton', 'AutoChainCheckBox',
    'RunPipelineButton', 'RunSpButton', 'RunShellButton',
    'Step1BadgeBorder', 'Step1BadgeText', 'Step2BadgeBorder', 'Step2BadgeText', 'Step3BadgeBorder', 'Step3BadgeText',
    'LiveLogBox', 'OpenOutputButton', 'CopySummaryButton'
)
$Controls = @{}
foreach ($n in $controlNames) { $Controls[$n] = $window.FindName($n) }

$script:State = @{
    Window                          = $window
    Controls                        = $Controls
    Settings                        = (Get-Settings)
    ZipPath                         = $null
    OutputFolder                    = $null
    Step1CompletedForCurrentInputs  = $false
    LogRawPath                      = $null
    LogSummaryPath                  = $null
    SummaryLines                    = New-Object System.Collections.ArrayList
}

if ($script:State.Settings.LastZipPath) { $Controls.ZipPathBox.Text = $script:State.Settings.LastZipPath }
if ($script:State.Settings.LastOutputFolder) { $Controls.OutputFolderBox.Text = $script:State.Settings.LastOutputFolder }

Set-StepBadge $Controls.Step1BadgeBorder $Controls.Step1BadgeText 'Pending'
Set-StepBadge $Controls.Step2BadgeBorder $Controls.Step2BadgeText 'Pending'
Set-StepBadge $Controls.Step3BadgeBorder $Controls.Step3BadgeText 'Pending'

$script:State.CoreExePath = Get-OrPromptExePath -SettingKeyName 'CoreExePath' -Candidates (Get-CoreExeCandidates) -DisplayName 'PowerDocu.exe (core documentation generator)'
$script:State.EnricherExePath  = Get-OrPromptExePath -SettingKeyName 'EnricherExePath'  -Candidates (Get-EnricherExeCandidates)  -DisplayName 'PowerDocu.SharePointEnricher.exe (SharePoint enrichment tool)'
$script:State.ShellExePath = Get-OrPromptExePath -SettingKeyName 'ShellExePath' -Candidates (Get-ShellExeCandidates) -DisplayName 'PowerDocu.Shell.exe (custom view / reshell tool)'

# ---------------------------------------------------------------------------
# Event wiring
# ---------------------------------------------------------------------------

$Controls.BrowseZipButton.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = 'Choose a solution zip'
    $dlg.Filter = 'Solution zip (*.zip)|*.zip'
    if ($script:State.Controls.ZipPathBox.Text -and (Test-Path $script:State.Controls.ZipPathBox.Text)) {
        $dlg.InitialDirectory = Split-Path -Parent $script:State.Controls.ZipPathBox.Text
    }
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:State.Controls.ZipPathBox.Text = $dlg.FileName
        if (-not $script:State.Controls.OutputFolderBox.Text) {
            $script:State.Controls.OutputFolderBox.Text = Split-Path -Parent $dlg.FileName
        }
    }
})

$Controls.ChooseOutputButton.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Choose an output folder'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:State.Controls.OutputFolderBox.Text = $dlg.SelectedPath
    }
})

$Controls.ZipPathBox.Add_TextChanged({
    $script:State.Step1CompletedForCurrentInputs = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false
    $script:State.Controls.RunShellButton.IsEnabled = $false
})

$Controls.OutputFolderBox.Add_TextChanged({
    $script:State.Step1CompletedForCurrentInputs = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false
    $script:State.Controls.RunShellButton.IsEnabled = $false
})

$Controls.RunPipelineButton.Add_Click({
    $zip = $script:State.Controls.ZipPathBox.Text
    if (-not $zip -or -not (Test-Path $zip)) {
        [System.Windows.MessageBox]::Show('Choose a valid solution zip first.', 'Atlas PP Doc') | Out-Null
        return
    }
    $out = $script:State.Controls.OutputFolderBox.Text
    if (-not $out) {
        $out = Split-Path -Parent $zip
        $script:State.Controls.OutputFolderBox.Text = $out
    }

    $script:State.ZipPath = $zip
    $script:State.OutputFolder = $out
    $script:State.Settings.LastZipPath = $zip
    $script:State.Settings.LastOutputFolder = $out
    Save-Settings $script:State.Settings

    $session = New-LogSession
    $script:State.LogRawPath = $session.RawPath
    $script:State.LogSummaryPath = $session.SummaryPath
    $script:State.SummaryLines = New-Object System.Collections.ArrayList
    $script:State.Controls.LiveLogBox.Clear()

    Set-StepBadge $script:State.Controls.Step1BadgeBorder $script:State.Controls.Step1BadgeText 'Pending'
    Set-StepBadge $script:State.Controls.Step2BadgeBorder $script:State.Controls.Step2BadgeText 'Pending'
    Set-StepBadge $script:State.Controls.Step3BadgeBorder $script:State.Controls.Step3BadgeText 'Pending'
    $script:State.Controls.OpenOutputButton.IsEnabled = $false
    $script:State.Controls.CopySummaryButton.IsEnabled = $false
    $script:State.Controls.RunShellButton.IsEnabled = $false
    $script:State.Controls.RunPipelineButton.IsEnabled = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false

    Start-Step1
})

$Controls.RunSpButton.Add_Click({
    if (-not $script:State.Step1CompletedForCurrentInputs) { return }
    $script:State.Controls.RunPipelineButton.IsEnabled = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false
    Start-Step2
})

$Controls.RunShellButton.Add_Click({
    if (-not $script:State.Step1CompletedForCurrentInputs) { return }
    $script:State.Controls.RunPipelineButton.IsEnabled = $false
    $script:State.Controls.RunSpButton.IsEnabled = $false
    $script:State.Controls.RunShellButton.IsEnabled = $false
    Start-Step3
})

$Controls.OpenOutputButton.Add_Click({
    $path = $script:State.OutputFolder
    if ($path -and (Test-Path $path)) {
        Start-Process explorer.exe -ArgumentList "`"$path`""
    }
})

$Controls.CopySummaryButton.Add_Click({
    $text = ($script:State.SummaryLines -join "`r`n")
    if ($text) {
        [System.Windows.Clipboard]::SetText($text)
        $script:State.Controls.CopySummaryButton.Content = 'Copied'
        $timer = New-Object System.Windows.Threading.DispatcherTimer
        $timer.Interval = [TimeSpan]::FromSeconds(1.5)
        $timer.Add_Tick({
            $script:State.Controls.CopySummaryButton.Content = 'Copy shareable summary'
            $timer.Stop()
        })
        $timer.Start()
    }
})

[void]$window.ShowDialog()
