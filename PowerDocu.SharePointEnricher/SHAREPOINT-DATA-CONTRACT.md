# SharePoint fetch data contract

`Resources\FetchSharePointData.ps1` is invoked once per enrichment run, given every
distinct site URL + list ID/name pair detected by `SharePointReferenceDetector`, and
must print exactly one JSON document to stdout matching this shape:

```json
[
  {
    "siteUrl": "https://contoso.sharepoint.com/sites/Example",
    "lists": [
      {
        "title": "VisitLog",
        "id": "a3c6dce5-185f-4ca8-beaa-122e81958a90",
        "columns": [
          {
            "internalName": "Title",
            "displayName": "Title",
            "type": "Text",
            "required": true,
            "choices": []
          }
        ],
        "sampleItems": [
          { "Title": "Example row", "Id": 1 }
        ]
      }
    ]
  }
]
```

`SharePointDataFetcher.cs` deserializes stdout against this exact contract into
`SharePointSiteEntity`/`SharePointListEntity`/`SharePointColumnEntity` objects. The
fetcher takes a `TextReader` as its real input — the live `pwsh.exe` process's stdout
in normal use, or any other `TextReader` producing this same JSON shape when the
caller needs to inject output some other way — but per the plan, validating this tool
means running the real script against a real site, not substituting fixture data.

## Row cap

`sampleItems` is capped (`-SampleLimit`, default 20) via a CAML `<RowLimit>` query,
**not** `-PageSize` on `Get-PnPListItem` — confirmed via documentation research
(a PnP PowerShell GitHub issue and Microsoft Q&A) that `-PageSize` only controls
per-request batch size and does not cap the total items returned; using it alone
would silently pull the entire list.

## Auth

One `Connect-PnPOnline -Interactive` browser prompt per distinct site URL (grouped
before connecting, so a solution referencing the same site from multiple lists/flows
still only prompts once per site).

## Failure modes

On `PnP.PowerShell` module missing, auth cancelled, or a list not found: the script
writes a clear message to stderr and exits non-zero for that site; `SharePointDataFetcher`
catches this, logs it, and continues without that site rather than aborting the whole run.
