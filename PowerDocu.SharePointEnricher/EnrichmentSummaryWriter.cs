using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Writes everything this run learned - sites, lists, and the flow/app reference
    /// index - as one new JSON file at the root of PowerDocu's existing output folder.
    /// Deliberately does not open, parse, or modify any file PowerDocu itself wrote:
    /// v1's OutputPatcher patched solution-*.html/md and each referencing flow/app's own
    /// HTML page in place via DOM/text surgery keyed to PowerDocu's current markup shape,
    /// which is exactly the coupling this rewrite removes. Cross-linking (e.g. "this flow
    /// references list X") now happens downstream, in the reshell tool, computed from this
    /// JSON rather than injected into PowerDocu's own files.
    /// </summary>
    public static class EnrichmentSummaryWriter
    {
        public static void Write(string outputFolder, List<SharePointSiteEntity> sites, List<SharePointReference> references,
            Dictionary<string, string> siteHtmlPaths, Dictionary<string, string> siteMdPaths)
        {
            var summary = new
            {
                sites = sites.Select(site => new
                {
                    siteUrl = site.SiteUrl,
                    htmlPath = siteHtmlPaths.TryGetValue(site.SiteUrl, out string h) ? h : null,
                    mdPath = siteMdPaths.TryGetValue(site.SiteUrl, out string m) ? m : null,
                    lists = site.Lists.Select(list => new
                    {
                        title = list.Title,
                        id = list.Id,
                        columnCount = list.Columns.Count,
                        sampleItemCount = list.SampleItems.Count,
                        referencedByFlows = list.ReferencedByFlows,
                        referencedByApps = list.ReferencedByApps
                    })
                }),
                references = references.Select(r => new
                {
                    sourceType = r.SourceType,
                    sourceName = r.SourceName,
                    actionOrDataSourceName = r.ActionOrDataSourceName,
                    siteUrl = r.SiteUrl,
                    listIdOrName = r.ListIdOrName,
                    listTitle = sites.SelectMany(s => s.Lists).FirstOrDefault(l => l.Id == r.ListIdOrName)?.Title,
                    isListIdConfident = r.IsListIdConfident
                })
            };

            string path = Path.Combine(outputFolder, "sharepoint-references.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(summary, Formatting.Indented));
        }
    }
}
