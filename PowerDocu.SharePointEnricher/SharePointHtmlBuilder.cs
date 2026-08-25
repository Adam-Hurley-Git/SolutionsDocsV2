using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PowerDocu.Common;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Extends PowerDocu.Common's HtmlBuilder (unmodified base class) and calls the
    /// inherited WrapInHtmlPage()/WriteDefaultStylesheet() — automatically inherits the
    /// fork's branding/CSS/nav-tech-icon styling, since it's the exact same compiled base
    /// class every native PowerDocu HTML builder uses. One page per site, at
    /// "SharePointDoc <SafeSiteName>\index-<safesitename>.html", with each list as its
    /// own anchored section (columns table + capped sample-data table).
    /// </summary>
    public class SharePointHtmlBuilder : HtmlBuilder
    {
        public readonly Dictionary<string, string> SiteHtmlPaths = new Dictionary<string, string>();

        private readonly string solutionFolderPath;
        private readonly string solutionDocHtmlRelativePath;

        public SharePointHtmlBuilder(List<SharePointSiteEntity> sites, List<SharePointReference> references, string solutionFolderPath, string solutionDocHtmlRelativePath)
        {
            this.solutionFolderPath = solutionFolderPath;
            this.solutionDocHtmlRelativePath = solutionDocHtmlRelativePath;

            foreach (SharePointSiteEntity site in sites)
            {
                string safeSiteName = CharsetHelper.GetSafeName(site.SiteUrl);
                string siteFolder = Path.Combine(solutionFolderPath, "SharePointDoc " + safeSiteName);
                Directory.CreateDirectory(siteFolder);
                WriteDefaultStylesheet(siteFolder);

                string fileName = ("index-" + safeSiteName + ".html").Replace(" ", "-");
                string relativePath = "SharePointDoc " + safeSiteName + "/" + fileName;
                SiteHtmlPaths[site.SiteUrl] = relativePath;

                string body = BuildSiteBody(site, references);
                string nav = BuildNavigation(site);
                SaveHtmlFile(Path.Combine(siteFolder, fileName), WrapInHtmlPage("SharePoint - " + site.SiteUrl, body, nav, "../style.css"));

                NotificationHelper.SendNotification("Created HTML documentation for SharePoint site " + site.SiteUrl);
            }
        }

        private string BuildSiteBody(SharePointSiteEntity site, List<SharePointReference> references)
        {
            StringBuilder body = new StringBuilder();
            body.AppendLine(Heading(1, "SharePoint: " + site.SiteUrl));
            body.AppendLine(Paragraph($"This site has {site.Lists.Count} list(s) referenced by this solution."));

            body.AppendLine(HeadingWithId(2, "Lists", "lists"));
            body.Append(TableStart("List", "Columns", "Sample Rows"));
            foreach (SharePointListEntity list in site.Lists)
            {
                string anchor = SanitizeAnchorId("list-" + (list.Title ?? list.Id));
                body.Append(TableRowRaw(
                    Link(list.Title ?? list.Id, "#" + anchor),
                    list.Columns.Count.ToString(),
                    list.SampleItems.Count.ToString()));
            }
            body.AppendLine(TableEnd());

            foreach (SharePointListEntity list in site.Lists)
            {
                string anchor = SanitizeAnchorId("list-" + (list.Title ?? list.Id));
                body.AppendLine(HeadingWithId(2, list.Title ?? list.Id, anchor));
                body.Append(TableStart("Property", "Value"));
                body.Append(TableRow("Title", list.Title ?? ""));
                body.Append(TableRow("List ID", list.Id ?? ""));
                body.AppendLine(TableEnd());

                if (list.Columns.Count > 0)
                {
                    body.AppendLine(Heading(3, "Columns"));
                    body.Append(TableStart("Display Name", "Internal Name", "Type", "Required", "Choices"));
                    foreach (SharePointColumnEntity col in list.Columns)
                    {
                        body.Append(TableRow(col.DisplayName ?? "", col.InternalName ?? "", col.TypeAsString ?? "",
                            col.Required ? "Yes" : "No", string.Join(", ", col.Choices)));
                    }
                    body.AppendLine(TableEnd());
                }

                if (list.SampleItems.Count > 0)
                {
                    body.AppendLine(Heading(3, $"Sample Data ({list.SampleItems.Count} row(s))"));
                    List<string> columnKeys = list.SampleItems.SelectMany(i => i.Keys).Distinct().ToList();
                    body.Append(TableStart(columnKeys.ToArray()));
                    foreach (Dictionary<string, object> row in list.SampleItems)
                    {
                        body.Append(TableRow(columnKeys.Select(k => row.TryGetValue(k, out object v) ? v?.ToString() ?? "" : "").ToArray()));
                    }
                    body.AppendLine(TableEnd());
                }

                // Reverse cross-links (plan 3.5): which flows/apps use this list.
                var usedBy = references.Where(r => r.SiteUrl == site.SiteUrl && r.ListIdOrName == list.Id).ToList();
                if (usedBy.Count > 0)
                {
                    body.AppendLine(Heading(3, "Used By"));
                    body.AppendLine(BulletListStart());
                    foreach (SharePointReference reference in usedBy)
                    {
                        string linkPath = reference.SourceType == "Flow"
                            ? "../" + CrossDocLinkHelper.GetFlowDocHtmlPath(reference.SourceName)
                            : "../" + CrossDocLinkHelper.GetAppDocHtmlPath(reference.SourceName);
                        body.AppendLine(BulletItemRaw($"{reference.SourceType}: " + Link(reference.SourceName, linkPath)));
                    }
                    body.AppendLine(BulletListEnd());
                }
            }

            return body.ToString();
        }

        private string BuildNavigation(SharePointSiteEntity site)
        {
            var navItems = new List<(string label, string href, int level)>
            {
                ("← Solution", "../" + solutionDocHtmlRelativePath, 0),
                ("SharePoint", "#lists", 0)
            };
            foreach (SharePointListEntity list in site.Lists)
            {
                string anchor = SanitizeAnchorId("list-" + (list.Title ?? list.Id));
                navItems.Add((list.Title ?? list.Id, "#" + anchor, 1));
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<div class=\"nav-title\">{Encode(site.SiteUrl)}</div>");
            sb.Append(NavigationList(navItems));
            return sb.ToString();
        }
    }
}
