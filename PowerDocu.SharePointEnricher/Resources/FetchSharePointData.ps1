<#
.SYNOPSIS
    Fetches SharePoint list schema + a capped sample of rows for the sites/lists a
    Power Platform solution references, and prints the result as JSON on stdout
    matching SHAREPOINT-DATA-CONTRACT.md.

.PARAMETER Requests
    JSON array of { siteUrl, listId } pairs to fetch, e.g.
    '[{"siteUrl":"https://contoso.sharepoint.com/sites/Example","listId":"<guid>"}]'

.PARAMETER SampleLimit
    Maximum rows to return per list (default 20). Enforced via a CAML <RowLimit>
    query on Get-PnPListItem, not -PageSize — see SHAREPOINT-DATA-CONTRACT.md for why.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Requests,

    [int]$SampleLimit = 20
)

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name PnP.PowerShell)) {
    Write-Error "PnP.PowerShell module is not installed. Install it with: Install-Module PnP.PowerShell -Scope CurrentUser"
    exit 1
}

Import-Module PnP.PowerShell -ErrorAction Stop

$requestList = $Requests | ConvertFrom-Json
$bySite = $requestList | Group-Object -Property siteUrl

$result = @()

foreach ($siteGroup in $bySite) {
    $siteUrl = $siteGroup.Name
    try {
        Connect-PnPOnline -Url $siteUrl -Interactive -ErrorAction Stop
    }
    catch {
        Write-Error "Failed to connect to '$siteUrl': $_"
        continue
    }

    $lists = @()
    foreach ($req in $siteGroup.Group) {
        try {
            $spList = Get-PnPList -Identity $req.listId -Includes Fields -ErrorAction Stop
        }
        catch {
            Write-Error "List '$($req.listId)' not found on '$siteUrl': $_"
            continue
        }

        $columns = $spList.Fields | Where-Object { -not $_.Hidden } | ForEach-Object {
            [PSCustomObject]@{
                internalName = $_.InternalName
                displayName  = $_.Title
                type         = $_.TypeAsString
                required     = [bool]$_.Required
                choices      = @(if ($_.TypeAsString -eq "Choice" -or $_.TypeAsString -eq "MultiChoice") { $_.Choices } else { @() })
            }
        }

        # RowLimit, not -PageSize: -PageSize only controls per-request batch size and
        # does not cap the total number of items returned (see SHAREPOINT-DATA-CONTRACT.md).
        $camlQuery = "<View><RowLimit>$SampleLimit</RowLimit></View>"
        $items = Get-PnPListItem -List $spList -Query $camlQuery -ErrorAction Stop

        $sampleItems = $items | ForEach-Object {
            $row = @{}
            foreach ($key in $_.FieldValues.Keys) {
                $row[$key] = $_.FieldValues[$key]
            }
            [PSCustomObject]$row
        }

        $lists += [PSCustomObject]@{
            title       = $spList.Title
            id          = $spList.Id.ToString()
            columns     = @($columns)
            sampleItems = @($sampleItems)
        }
    }

    $result += [PSCustomObject]@{
        siteUrl = $siteUrl
        lists   = @($lists)
    }
}

$result | ConvertTo-Json -Depth 6
