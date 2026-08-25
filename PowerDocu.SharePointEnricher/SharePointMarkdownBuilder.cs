using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grynwald.MarkdownGenerator;
using PowerDocu.Common;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Extends PowerDocu.Common's MarkdownBuilder, mirroring SharePointHtmlBuilder's
    /// structure/content exactly, so both output formats stay consistent.
    /// </summary>
    public class SharePointMarkdownBuilder : MarkdownBuilder
    {
        public readonly Dictionary<string, string> SiteMdPaths = new Dictionary<string, string>();

        public SharePointMarkdownBuilder(List<SharePointSiteEntity> sites, List<SharePointReference> references, string solutionFolderPath, string solutionDocMdRelativePath)
        {
            foreach (SharePointSiteEntity site in sites)
            {
                string safeSiteName = CharsetHelper.GetSafeName(site.SiteUrl);
                string siteFolder = Path.Combine(solutionFolderPath, "SharePointDoc " + safeSiteName);
                Directory.CreateDirectory(siteFolder);

                string fileName = "index-" + safeSiteName + ".md";
                string relativePath = "SharePointDoc " + safeSiteName + "/" + fileName;
                SiteMdPaths[site.SiteUrl] = relativePath;

                MdDocument doc = new MdDocument();
                doc.Root.Add(new MdHeading("SharePoint: " + site.SiteUrl, 1));
                doc.Root.Add(new MdParagraph($"This site has {site.Lists.Count} list(s) referenced by this solution."));
                doc.Root.Add(new MdParagraph(new MdLinkSpan("← Solution", "../" + solutionDocMdRelativePath)));

                doc.Root.Add(new MdHeading("Lists", 2));
                List<MdTableRow> summaryRows = site.Lists.Select(list =>
                    new MdTableRow(list.Title ?? list.Id, list.Columns.Count.ToString(), list.SampleItems.Count.ToString())).ToList();
                doc.Root.Add(new MdTable(new MdTableRow("List", "Columns", "Sample Rows"), summaryRows));

                foreach (SharePointListEntity list in site.Lists)
                {
                    doc.Root.Add(new MdHeading(list.Title ?? list.Id, 2));
                    doc.Root.Add(new MdTable(new MdTableRow("Property", "Value"), new List<MdTableRow>
                    {
                        new MdTableRow("Title", list.Title ?? ""),
                        new MdTableRow("List ID", list.Id ?? "")
                    }));

                    if (list.Columns.Count > 0)
                    {
                        doc.Root.Add(new MdHeading("Columns", 3));
                        List<MdTableRow> colRows = list.Columns.Select(col => new MdTableRow(
                            col.DisplayName ?? "", col.InternalName ?? "", col.TypeAsString ?? "",
                            col.Required ? "Yes" : "No", string.Join(", ", col.Choices))).ToList();
                        doc.Root.Add(new MdTable(new MdTableRow("Display Name", "Internal Name", "Type", "Required", "Choices"), colRows));
                    }

                    if (list.SampleItems.Count > 0)
                    {
                        doc.Root.Add(new MdHeading($"Sample Data ({list.SampleItems.Count} row(s))", 3));
                        List<string> columnKeys = list.SampleItems.SelectMany(i => i.Keys).Distinct().ToList();
                        List<MdTableRow> dataRows = list.SampleItems.Select(row =>
                            new MdTableRow(columnKeys.Select(k => row.TryGetValue(k, out object v) ? v?.ToString() ?? "" : "").ToList())).ToList();
                        doc.Root.Add(new MdTable(new MdTableRow(columnKeys), dataRows));
                    }

                    var usedBy = references.Where(r => r.SiteUrl == site.SiteUrl && r.ListIdOrName == list.Id).ToList();
                    if (usedBy.Count > 0)
                    {
                        doc.Root.Add(new MdHeading("Used By", 3));
                        List<MdListItem> items = usedBy.Select(reference =>
                        {
                            string linkPath = reference.SourceType == "Flow"
                                ? "../" + CrossDocLinkHelper.GetFlowDocMdPath(reference.SourceName)
                                : "../" + CrossDocLinkHelper.GetAppDocMdPath(reference.SourceName);
                            return new MdListItem(new MdLinkSpan(reference.SourceType + ": " + reference.SourceName, linkPath));
                        }).ToList();
                        doc.Root.Add(new MdBulletList(items));
                    }
                }

                doc.Save(Path.Combine(solutionFolderPath, relativePath));
                NotificationHelper.SendNotification("Created Markdown documentation for SharePoint site " + site.SiteUrl);
            }
        }
    }
}
