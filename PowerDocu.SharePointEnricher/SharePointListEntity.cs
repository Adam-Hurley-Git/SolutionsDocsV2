using System.Collections.Generic;

namespace PowerDocu.SharePointEnricher
{
    public class SharePointListEntity
    {
        public string Title;
        public string Id;
        public List<SharePointColumnEntity> Columns = new List<SharePointColumnEntity>();

        /// <summary>
        /// Capped sample of rows (default 20 — see FetchSharePointData.ps1's -SampleLimit),
        /// not a full data dump. Each dictionary is one row, keyed by column internal name.
        /// </summary>
        public List<Dictionary<string, object>> SampleItems = new List<Dictionary<string, object>>();

        /// <summary>
        /// Flows/Apps (by name) detected as referencing this list, populated by
        /// SharePointReferenceDetector and consumed by SharePointHtmlBuilder/
        /// SharePointMarkdownBuilder for the reverse "Used by" cross-links, and by
        /// EnrichmentSummaryWriter for the same data in sharepoint-references.json.
        /// </summary>
        public List<string> ReferencedByFlows = new List<string>();
        public List<string> ReferencedByApps = new List<string>();
    }
}
