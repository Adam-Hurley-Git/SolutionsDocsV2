# SharePoint fetch mechanism

`SharePointDataFetcher.cs` fetches directly via SharePoint CSOM
(`Microsoft.SharePoint.Client`), authenticating interactively through
PnP.Framework/MSAL - no `pwsh.exe`, no `PnP.PowerShell` module, nothing to
install to run this exe. This replaced an earlier version that shelled out to
a PowerShell script (`Resources\FetchSharePointData.ps1`, now removed) which
did the same fetch via `PnP.PowerShell` cmdlets; the shape of what gets
fetched is unchanged, only the mechanism is.

For each distinct site URL, one `AuthenticationManager.CreateWithInteractiveLogin`
sign-in (grouped before connecting, so a solution referencing the same site
from multiple lists/flows still only prompts once per site). For each
distinct list requested on that site: the list's title/ID, its non-hidden
fields (internal name, display name, type, required, and choices for
Choice/MultiChoice fields), and a capped sample of rows.

## One requirement no language choice removes

Since September 2024, Microsoft retired the shared multi-tenant "PnP
Management Shell" Entra ID app that let `Connect-PnPOnline -Interactive`
(and, equally, `AuthenticationManager.CreateWithInteractiveLogin`) work with
zero setup. Every tenant now needs its **own** Entra ID app registration - a
one-time admin action - and its Client ID passed to this tool as the
`clientId` argument. This is a Microsoft platform requirement, not a
limitation of the PowerShell or C# implementation - see HANDOFF.md for the
exact registration steps.

## Row cap

`sampleItems` is capped (`sampleLimit`, default 20) via a CAML `<RowLimit>`
query on `List.GetItems`, **not** a client-side `Take()` after fetching
everything - confirmed via documentation research that pulling everything
and truncating client-side would defeat the purpose of a sample cap for a
large list.

## Failure modes

On auth cancelled, a list not found, or no access: `FetchList`/`FetchLiveAsync`
catch the exception, log a clear message to stderr, and continue without that
site/list rather than aborting the whole run.
