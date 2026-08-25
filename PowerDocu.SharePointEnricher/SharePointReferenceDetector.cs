using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PowerDocu.Common;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Detects references to SharePoint sites/lists inside already-parsed Flow/App entities,
    /// by calling PowerDocu's existing public parse entry points (read-only — this project
    /// never touches SolutionDocumenter/ConfigHelper/the core CLI/GUI, per the plan's
    /// isolation requirement).
    ///
    /// Field-shape confirmation (plan 3.2 required this before writing the extractor,
    /// rather than coding against an assumed name — done against two genuine published
    /// samples, not fabricated fixtures):
    ///
    /// FLOWS — two real shapes were found, from exports six years apart:
    ///   - Modern (confirmed against a real 2025 export, manish-parashar/PowerAutomate):
    ///     action.inputs = { "host": {...}, "parameters": { "dataset": "<site url>", "table": "<list GUID>" }, ... }
    ///     PowerDocu's FlowParser turns "parameters" into one Expression whose expressionOperands
    ///     are child Expressions named "dataset" / "table" (see ExtractModernShape below).
    ///   - Legacy (confirmed against a real 2019 export, pnp/sp-power-platform-solutions'
    ///     SiteVisitTracker sample): type "ApiConnection" (not "OpenApiConnection"), no
    ///     "parameters" node at all — the site URL is instead double-URL-encoded inside a
    ///     "path" string, e.g. .../datasets/@{encodeURIComponent(encodeURIComponent('https://.../sites/X'))}/...
    ///     There is no clean list GUID in this shape, only a "queries.folderPath" (a document
    ///     library path, not a list identity) — so results from this shape are flagged
    ///     IsListIdConfident = false rather than presented with false certainty.
    ///   PowerDocu's own FlowParser (see FlowParser.cs's extractConnectorName) resolves
    ///   ActionNode.Connection / Trigger.Connector down to the bare connector name
    ///   "sharepointonline" for the modern shape and for the legacy shape's *common*
    ///   "@parameters('$connections')['shared_...']" connection-reference form.
    ///
    ///   CONFIRMED GAP, NOW CLOSED (found by running this detector against the real
    ///   SiteVisitTracker sample, not caught until real-data testing): that same sample's
    ///   flow uses a third, older connection-reference form —
    ///   "@json(decodeBase64(triggerOutputs().headers[...]))['$connections']
    ///   ['shared_sharepointonline']['connectionId']" (a PowerApp-triggered flow calling
    ///   back into SharePoint, which web research confirms is Microsoft's standard,
    ///   documented pattern for *any* Canvas-App-triggered flow, not a one-off oddity).
    ///   Core extractConnectorName's string-replace logic only unwraps the
    ///   "@parameters('$connections')[...]" form, so for this shape ActionNode.Connection
    ///   is left as a raw fragment of that expression instead of "sharepointonline".
    ///
    ///   Rather than pattern-match each wrapper variant as it turns up (which only ever
    ///   covers shapes already seen), detection no longer *depends* on ActionNode.Connection
    ///   being resolved at all. Three independent signals now run, any one of which is
    ///   enough to accept a candidate (see AcceptCandidate):
    ///     1. Local connector normalization (NormalizeConnectorName) — pulls the connector
    ///        name out of "shared_<name>" wherever it appears in the string, so every
    ///        wrapper form (including ones not yet seen) resolves the same way. Done here,
    ///        not in core: extractConnectorName is shared by all of PowerDocu, and changing
    ///        it would alter Connection for every connector everywhere (icons, hyperlinks,
    ///        graphs) and add upstream-merge surface, for no gain this project needs.
    ///     2. Flow-level declaration (FlowDeclaresSharePoint) — FlowEntity.connectionReferences
    ///        comes from the flow's top-level "connectionReferences" metadata block, whose
    ///        values reach extractConnectorName as literals ("shared_sharepointonline" /
    ///        "/providers/Microsoft.PowerApps/apis/shared_sharepointonline") and so hit its
    ///        clean StartsWith/Contains branches, never the fragile "@"-expression branch.
    ///        This makes it a reliable "does this flow touch SharePoint at all" signal.
    ///     3. Value shape (LooksLikeSharePointSiteUrl) — a site URL on a *.sharepoint.<tld>
    ///        host sitting in a recognized parameter position is definitionally a SharePoint
    ///        reference regardless of what the action claims its connector is. Restricted to
    ///        recognized positions (parameters.*, path) rather than scanned across the whole
    ///        action, so a SharePoint URL merely quoted in an email body never matches.
    ///
    ///   Known remaining limit: a flow reaching SharePoint through the generic Http action
    ///   (or a premium HTTP connector) while declaring no SharePoint connection anywhere
    ///   offers no signal beyond a bare URL, which is genuinely ambiguous. Deliberately out
    ///   of scope — catching it would mean accepting false positives.
    ///
    /// CANVAS APPS (confirmed against the real .msapp inside the SiteVisitTracker sample's
    /// SiteVisitTrackerApp.zip — a real "VisitLog" list data source):
    ///   DataSources.json entry: { "Name": "VisitLog", "Type": "ConnectedDataSourceInfo",
    ///     "DatasetName": "<site url>", "TableName": "<list GUID>", "ApiId": ".../shared_sharepointonline" }
    ///   PowerDocu's AppParser puts every key except Name/Type into DataSource.Properties as
    ///   top-level Expressions, so "DatasetName"/"TableName"/"ApiId" are each a simple
    ///   Expression with a single string operand.
    /// </summary>
    public static class SharePointReferenceDetector
    {
        public static List<SharePointReference> Detect(List<FlowEntity> flows, List<AppEntity> apps)
        {
            var results = new List<SharePointReference>();
            if (flows != null) results.AddRange(DetectInFlows(flows));
            if (apps != null) results.AddRange(DetectInApps(apps));
            return results;
        }

        private static List<SharePointReference> DetectInFlows(List<FlowEntity> flows)
        {
            var results = new List<SharePointReference>();
            foreach (FlowEntity flow in flows)
            {
                // Signal 2 (see class summary): reliable per-flow "touches SharePoint at all",
                // read from metadata that never goes through the fragile expression unwrapping.
                bool flowDeclaresSharePoint = FlowDeclaresSharePoint(flow);

                if (flow.trigger != null)
                {
                    var reference = ExtractFromInputs(flow.trigger.Inputs, "Flow", flow.Name, flow.trigger.Name,
                        IsSharePointConnector(flow.trigger.Connector) || flowDeclaresSharePoint);
                    if (reference != null) results.Add(reference);
                }

                foreach (ActionNode node in flow.actions.ActionNodes)
                {
                    var reference = ExtractFromInputs(node.actionInputs, "Flow", flow.Name, node.Name,
                        IsSharePointConnector(node.Connection) || flowDeclaresSharePoint);
                    if (reference != null) results.Add(reference);
                }
            }
            return Deduplicate(results);
        }

        /// <summary>
        /// True if the flow's top-level connection-reference metadata declares the SharePoint
        /// connector. See signal 2 in the class summary for why this source is reliable where
        /// the per-action ActionNode.Connection is not.
        /// </summary>
        private static bool FlowDeclaresSharePoint(FlowEntity flow)
        {
            return flow.connectionReferences?.Any(cRef => IsSharePointConnector(cRef.Name)) == true;
        }

        private static List<SharePointReference> DetectInApps(List<AppEntity> apps)
        {
            var results = new List<SharePointReference>();
            foreach (AppEntity app in apps)
            {
                foreach (DataSource ds in app.DataSources)
                {
                    if (ds.Type?.Equals("ConnectedDataSourceInfo", StringComparison.OrdinalIgnoreCase) != true) continue;

                    string datasetName = GetPropertyStringValue(ds.Properties, "DatasetName");
                    string tableName = GetPropertyStringValue(ds.Properties, "TableName");
                    if (string.IsNullOrEmpty(datasetName) || string.IsNullOrEmpty(tableName)) continue;

                    // Same two-signal acceptance as flows: the declared connector, or a site URL
                    // that is self-evidently SharePoint. Apps have no expression-wrapper problem
                    // (ApiId is a clean literal here), so this is belt-and-braces rather than a fix.
                    string apiId = GetPropertyStringValue(ds.Properties, "ApiId");
                    if (!IsSharePointConnector(apiId) && !LooksLikeSharePointSiteUrl(datasetName)) continue;

                    results.Add(new SharePointReference
                    {
                        SiteUrl = datasetName,
                        ListIdOrName = tableName,
                        IsListIdConfident = true,
                        SourceType = "App",
                        SourceName = app.Name,
                        ActionOrDataSourceName = ds.Name
                    });
                }
            }
            return Deduplicate(results);
        }

        /// <summary>
        /// Tries the modern OpenApiConnection "parameters.dataset/table" shape first, then the
        /// legacy ApiConnection "path"-encoded shape, then a generic scan of the action's
        /// recognized parameter positions. Whatever a shape finds is only a *candidate* —
        /// AcceptCandidate decides, so that a shape which happens to look similar for a
        /// non-SharePoint connector is not taken on the shape alone.
        /// </summary>
        /// <param name="connectorCorroborates">
        /// True when either this action's own (normalized) connector name or its flow's
        /// connection-reference metadata says SharePoint — signals 1 and 2 in the class summary.
        /// </param>
        private static SharePointReference ExtractFromInputs(List<Expression> inputs, string sourceType, string sourceName, string actionName, bool connectorCorroborates)
        {
            SharePointReference candidate =
                ExtractModernShape(inputs, sourceType, sourceName, actionName)
                ?? ExtractLegacyShape(inputs, sourceType, sourceName, actionName)
                ?? ExtractFromParameterValueShape(inputs, sourceType, sourceName, actionName);

            return AcceptCandidate(candidate, connectorCorroborates) ? candidate : null;
        }

        /// <summary>
        /// A candidate is accepted if the connector/flow metadata corroborates it (signals 1-2)
        /// or if its site URL is self-evidently a SharePoint one (signal 3). Requiring neither
        /// would let any connector using a similar parameter shape through; requiring both would
        /// reintroduce the very dependency on resolved connector names this design removes.
        /// </summary>
        private static bool AcceptCandidate(SharePointReference candidate, bool connectorCorroborates)
        {
            if (candidate == null) return false;
            return connectorCorroborates || LooksLikeSharePointSiteUrl(candidate.SiteUrl);
        }

        private static SharePointReference ExtractModernShape(List<Expression> inputs, string sourceType, string sourceName, string actionName)
        {
            Expression parameters = inputs?.FirstOrDefault(e => e.expressionOperator.Equals("parameters", StringComparison.OrdinalIgnoreCase));
            if (parameters == null) return null;

            string dataset = GetChildStringValue(parameters, "dataset");
            string table = GetChildStringValue(parameters, "table");
            if (string.IsNullOrEmpty(dataset) || string.IsNullOrEmpty(table)) return null;

            return new SharePointReference
            {
                SiteUrl = dataset,
                ListIdOrName = table,
                IsListIdConfident = true,
                SourceType = sourceType,
                SourceName = sourceName,
                ActionOrDataSourceName = actionName
            };
        }

        // Matches the URL literal inside encodeURIComponent(encodeURIComponent('<url>')) —
        // real exported flows double-encode the site URL this way in the legacy shape.
        private static readonly Regex LegacyDatasetUrlPattern = new Regex(
            @"encodeURIComponent\(encodeURIComponent\('([^']+)'\)\)", RegexOptions.Compiled);

        private static SharePointReference ExtractLegacyShape(List<Expression> inputs, string sourceType, string sourceName, string actionName)
        {
            Expression pathExpr = inputs?.FirstOrDefault(e => e.expressionOperator.Equals("path", StringComparison.OrdinalIgnoreCase));
            string pathValue = pathExpr?.expressionOperands?.FirstOrDefault() as string;
            if (string.IsNullOrEmpty(pathValue)) return null;

            Match match = LegacyDatasetUrlPattern.Match(pathValue);
            if (!match.Success) return null;

            // No reliable list GUID exists in this shape — folderPath (a document library
            // path) is the closest available identifier, and only for file-oriented actions.
            Expression queries = inputs.FirstOrDefault(e => e.expressionOperator.Equals("queries", StringComparison.OrdinalIgnoreCase));
            string folderPath = GetChildStringValue(queries, "folderPath");

            return new SharePointReference
            {
                SiteUrl = match.Groups[1].Value,
                ListIdOrName = folderPath ?? "(unknown - legacy connector format)",
                IsListIdConfident = false,
                SourceType = sourceType,
                SourceName = sourceName,
                ActionOrDataSourceName = actionName
            };
        }

        // Matches a SharePoint site URL by host. Covers the sovereign clouds too
        // (sharepoint.us / .de / .cn) rather than hardcoding the commercial .com.
        private static readonly Regex SharePointSiteUrlPattern = new Regex(
            @"^https://[^/\s]+\.sharepoint\.[a-z]{2,6}(?:/|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool LooksLikeSharePointSiteUrl(string value)
        {
            return !string.IsNullOrEmpty(value) && SharePointSiteUrlPattern.IsMatch(value);
        }

        /// <summary>
        /// Last-resort shape: any direct child of "parameters" whose value is a SharePoint site
        /// URL. Catches actions that genuinely use the SharePoint connector but carry no
        /// dataset/table pair — most notably "Send an HTTP request to SharePoint", whose inputs
        /// are a raw uri/method/body instead. Deliberately scoped to direct children of
        /// "parameters" rather than the whole action tree, so a site URL merely quoted inside an
        /// email body or a Compose never produces a reference.
        /// </summary>
        private static SharePointReference ExtractFromParameterValueShape(List<Expression> inputs, string sourceType, string sourceName, string actionName)
        {
            Expression parameters = inputs?.FirstOrDefault(e => e.expressionOperator.Equals("parameters", StringComparison.OrdinalIgnoreCase));
            if (parameters == null) return null;

            foreach (object operand in parameters.expressionOperands)
            {
                if (operand is not Expression child) continue;
                if (child.expressionOperands.FirstOrDefault() is not string value) continue;
                if (!LooksLikeSharePointSiteUrl(value)) continue;

                // A list identity may or may not be present in this shape — record what is
                // actually there rather than inferring one, same honesty rule as the legacy shape.
                string table = GetChildStringValue(parameters, "table");
                return new SharePointReference
                {
                    SiteUrl = value,
                    ListIdOrName = table ?? "(unknown - no list identifier in this action)",
                    IsListIdConfident = table != null,
                    SourceType = sourceType,
                    SourceName = sourceName,
                    ActionOrDataSourceName = actionName
                };
            }
            return null;
        }

        private static string GetChildStringValue(Expression parent, string childOperator)
        {
            if (parent == null) return null;
            foreach (object operand in parent.expressionOperands)
            {
                if (operand is Expression child && child.expressionOperator.Equals(childOperator, StringComparison.OrdinalIgnoreCase))
                {
                    return child.expressionOperands.FirstOrDefault() as string;
                }
            }
            return null;
        }

        private static string GetPropertyStringValue(List<Expression> properties, string operatorName)
        {
            Expression match = properties?.FirstOrDefault(e => e.expressionOperator.Equals(operatorName, StringComparison.OrdinalIgnoreCase));
            return match?.expressionOperands?.FirstOrDefault() as string;
        }

        // Pulls the connector name out of a "shared_<name>" token wherever it sits in a string.
        // Alphanumeric-only capture stops at the "_1"/"_2" suffixes Power Automate appends when
        // one flow holds several connections of the same connector, so those normalize together.
        private static readonly Regex SharedConnectorNamePattern = new Regex(
            @"shared_([A-Za-z0-9]+)", RegexOptions.Compiled);

        /// <summary>
        /// Resolves a connection string to a bare connector name, independent of which
        /// expression wrapper encloses it — signal 1 in the class summary. Values core PowerDocu
        /// already normalized (a bare "sharepointonline") pass through untouched.
        /// </summary>
        private static string NormalizeConnectorName(string connection)
        {
            if (string.IsNullOrEmpty(connection)) return connection;
            Match match = SharedConnectorNamePattern.Match(connection);
            return match.Success ? match.Groups[1].Value : connection;
        }

        private static bool IsSharePointConnector(string connection)
        {
            string normalized = NormalizeConnectorName(connection);
            return !string.IsNullOrEmpty(normalized) && normalized.Equals("sharepointonline", StringComparison.OrdinalIgnoreCase);
        }

        private static List<SharePointReference> Deduplicate(List<SharePointReference> references)
        {
            return references
                .GroupBy(r => (r.SiteUrl, r.ListIdOrName, r.SourceType, r.SourceName, r.ActionOrDataSourceName))
                .Select(g => g.First())
                .ToList();
        }
    }
}
