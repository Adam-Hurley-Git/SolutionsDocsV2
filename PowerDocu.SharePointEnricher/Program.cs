using System;
using System.IO;
using System.Linq;
using PowerDocu.AppDocumenter;
using PowerDocu.Common;
using PowerDocu.FlowDocumenter;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Standalone post-processing tool: runs *after* a normal PowerDocu run has already
    /// completed. Never modifies, and is never referenced by, PowerDocu.SolutionDocumenter,
    /// ConfigHelper, the CLI, or the GUI — see PowerDocu.SharePointEnricher.csproj's header
    /// comment for why.
    ///
    /// Usage:
    ///   PowerDocu.SharePointEnricher &lt;solution.zip&gt; &lt;existing-output-folder&gt; [sampleLimit]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: SolutionsDocs.SharePointEnricher <solution.zip> <existing-output-folder> [sampleLimit]");
                return 1;
            }

            string zipPath = args[0];
            string outputFolder = args[1];
            int sampleLimit = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : 20;

            if (!File.Exists(zipPath))
            {
                Console.Error.WriteLine("Solution zip not found: " + zipPath);
                return 1;
            }
            if (!Directory.Exists(outputFolder))
            {
                Console.Error.WriteLine("Output folder not found (run Solutions Docs against the solution first): " + outputFolder);
                return 1;
            }

            Console.WriteLine("Detecting SharePoint references...");
            var (flows, _) = FlowDocumentationGenerator.ParseFlows(zipPath, null);
            var (apps, _) = AppDocumentationGenerator.ParseApps(zipPath, null);

            var references = SharePointReferenceDetector.Detect(flows, apps);
            if (references.Count == 0)
            {
                Console.WriteLine("No SharePoint references found in this solution. Nothing to enrich.");
                return 0;
            }

            Console.WriteLine($"Found {references.Count} SharePoint reference(s):");
            foreach (var reference in references)
            {
                string confidence = reference.IsListIdConfident ? "" : " (best-effort — legacy connector format, no confirmed list ID)";
                Console.WriteLine($"  {reference.SourceType} '{reference.SourceName}' -> {reference.SiteUrl} :: {reference.ListIdOrName}{confidence}");
            }

            var confidentRequests = references
                .Where(r => r.IsListIdConfident)
                .Select(r => (r.SiteUrl, r.ListIdOrName))
                .Distinct()
                .ToList();

            if (confidentRequests.Count == 0)
            {
                Console.WriteLine("No references had a confirmed list ID to fetch (all were best-effort legacy-format guesses). Stopping before the live fetch step.");
                return 0;
            }

            string scriptPath = Path.Combine(AppContext.BaseDirectory, "Resources", "FetchSharePointData.ps1");
            var fetcher = new SharePointDataFetcher(scriptPath, sampleLimit);
            var sites = fetcher.FetchLive(confidentRequests.Select(r => (r.SiteUrl, r.ListIdOrName)));

            if (sites.Count == 0)
            {
                Console.WriteLine("No SharePoint data was fetched (see errors above). Stopping before page generation.");
                return 0;
            }

            Console.WriteLine("Generating SharePoint documentation pages...");
            string solutionDocHtmlPath = Path.GetFileName(Directory.GetFiles(outputFolder, "solution-*.html").FirstOrDefault() ?? "");
            string solutionDocMdPath = Path.GetFileName(Directory.GetFiles(outputFolder, "solution-*.md").FirstOrDefault() ?? "");

            var htmlBuilder = new SharePointHtmlBuilder(sites, references, outputFolder, solutionDocHtmlPath);
            var mdBuilder = new SharePointMarkdownBuilder(sites, references, outputFolder, solutionDocMdPath);

            Console.WriteLine("Writing enrichment summary (sharepoint-references.json)...");
            EnrichmentSummaryWriter.Write(outputFolder, sites, references, htmlBuilder.SiteHtmlPaths, mdBuilder.SiteMdPaths);

            Console.WriteLine("Done. Nothing PowerDocu wrote was modified - only new SharePointDoc pages and sharepoint-references.json were added.");
            return 0;
        }
    }
}
