using System.Collections.Generic;

namespace PowerDocu.Shell
{
    /// <summary>Mirrors the JSON EnrichmentSummaryWriter.cs writes - see that file for the source of truth.</summary>
    public class SpManifest
    {
        public List<SpSite> Sites { get; set; } = new();
        public List<SpReference> References { get; set; } = new();
    }

    public class SpSite
    {
        public string SiteUrl { get; set; }
        public string HtmlPath { get; set; }
        public string MdPath { get; set; }
        public List<SpList> Lists { get; set; } = new();
    }

    public class SpList
    {
        public string Title { get; set; }
        public string Id { get; set; }
        public int ColumnCount { get; set; }
        public int SampleItemCount { get; set; }
        public List<string> ReferencedByFlows { get; set; } = new();
        public List<string> ReferencedByApps { get; set; } = new();
    }

    public class SpReference
    {
        public string SourceType { get; set; }
        public string SourceName { get; set; }
        public string ActionOrDataSourceName { get; set; }
        public string SiteUrl { get; set; }
        public string ListIdOrName { get; set; }
        public string ListTitle { get; set; }
        public bool IsListIdConfident { get; set; }
    }
}
