namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// One detected reference from a Flow action/trigger or a Canvas App data source
    /// to a SharePoint list. Confidence is tracked because two real, differently-shaped
    /// SharePoint connector formats were found in genuine published samples while writing
    /// this detector (see SharePointReferenceDetector for the full explanation) — the
    /// legacy shape's list identifier is a best-effort folder-path guess, not a confirmed
    /// list ID, so callers (page generation, cross-linking) can flag it as such rather
    /// than presenting it with the same certainty as a confirmed GUID.
    /// </summary>
    public class SharePointReference
    {
        public string SiteUrl;

        /// <summary>
        /// The list's internal GUID (modern Flow/App connector shape) or, for the legacy
        /// Flow shape, a best-effort folder/library path extracted from the action's
        /// "path" input — see <see cref="IsListIdConfident"/>.
        /// </summary>
        public string ListIdOrName;
        public bool IsListIdConfident = true;

        public string SourceType; // "Flow" or "App"
        public string SourceName;
        public string ActionOrDataSourceName;
    }
}
