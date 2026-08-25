using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerDocu.Shell
{
    /// <summary>
    /// Solutions Docs v2 - reshell tool (C# port of the original Shell/build.js prototype).
    ///
    /// Reads a PowerDocu output folder (and whatever the SharePoint enricher added
    /// alongside it - same folder, new files only) and builds a navigable/searchable
    /// HTML shell in a separate output folder. Read-only on its input: never opens a
    /// file in outputFolder for writing. Self-contained exe - nothing to install to run it.
    ///
    /// Design principle: inclusion is a total function, not a pattern match. Every
    /// single file found gets a page, by a render rule with no "unrecognized" branch
    /// (md -&gt; html, image -&gt; embed, anything else -&gt; download link) and a nav
    /// position that defaults to mirroring its folder path. A second, best-effort pass
    /// then upgrades whatever it recognizes (FlowDoc/AppDoc/etc. naming, tabs, TOC,
    /// SharePoint cross-references) - upgrades only, never a gate on inclusion.
    ///
    /// Usage: PowerDocu.Shell.exe &lt;powerdocuOutputFolder&gt; [shellOutputFolder]
    /// </summary>
    public static class Program
    {
        private const string AssetDirName = "assets";
        private const string ManifestName = "sharepoint-references.json";

        private static readonly string[] TabPrefixOrder =
        {
            "index", "connections", "variables", "triggersactions", "appdetails",
            "datasources", "resources", "screens", "controls"
        };
        private static readonly Dictionary<string, string> TabLabels = new()
        {
            ["index"] = "Overview",
            ["connections"] = "Connections",
            ["variables"] = "Variables",
            ["triggersactions"] = "Trigger & actions",
            ["appdetails"] = "App details",
            ["datasources"] = "Data sources",
            ["resources"] = "Resources",
            ["screens"] = "Screens",
            ["controls"] = "Controls"
        };

        // Type is the short code nav.js/style.css key their icons and color theming off
        // of (.nav-group[data-t="sp"], .stat[data-t="sp"], ICONS.sp, etc.) - only
        // SharePoint has a short code distinct from its tier key, matching the mockup's
        // original CSS/JS, which this tool's chrome is carried over from unchanged.
        private static readonly (string Key, string Type, string Label, int Order)[] KnownTiers =
        {
            ("flow", "flow", "Flows", 10),
            ("app", "app", "Apps", 20),
            ("agent", "agent", "Agents", 30),
            ("appmodule", "appmodule", "Model-driven apps", 40),
            ("bpf", "bpf", "Business process flows", 50),
            ("desktopflow", "desktopflow", "Desktop flows", 60),
            ("classicworkflow", "classicworkflow", "Classic workflows", 70),
            ("aimodel", "aimodel", "AI models", 80),
            ("dataflow", "dataflow", "Dataflows", 90),
            ("sharepoint", "sp", "SharePoint", 100)
        };

        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg" };

        private class FileNode
        {
            public string Abs;
            public string Rel;
            public string Kind; // "md" | "image" | "other"
            public string HtmlRel;
        }

        private class DocEntry
        {
            public string Id;
            public string Label;
            public string Href;
            public string Meta;
            public List<NavTabDto> Tabs;
            public List<(NavKidDto Kid, FileNode Node)> Kids = new();
            public FileNode EntryNode;
            public List<FileNode> AllNodesInDoc = new();
            public Dictionary<string, string> TabIdByNodeRel = new();
        }

        private class Group
        {
            public string Id;
            public string Type;
            public string Label;
            public int Order;
            public List<DocEntry> Docs = new();
        }

        private class RootDoc
        {
            public string Id;
            public string Label;
            public string Href;
            public FileNode Node;
        }

        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: PowerDocu.Shell.exe <powerdocuOutputFolder> [shellOutputFolder]");
                return 1;
            }

            string outputFolder = Path.GetFullPath(args[0]);
            if (!Directory.Exists(outputFolder))
            {
                Console.Error.WriteLine("Output folder not found: " + outputFolder);
                return 1;
            }
            string shellFolder = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(outputFolder, "Shell"));

            // -----------------------------------------------------------------
            // 1. DISCOVERY - guaranteed-inclusion floor. Pure enumeration, no interpretation.
            // -----------------------------------------------------------------
            List<(string Abs, string Rel)> allFiles = Discover(outputFolder, outputFolder, shellFolder);
            var manifestEntry = allFiles.FirstOrDefault(f => f.Rel == ManifestName);
            List<(string Abs, string Rel)> contentFiles = allFiles.Where(f => f.Rel != ManifestName).ToList();

            SpManifest manifest = new();
            if (manifestEntry.Abs != null)
            {
                try
                {
                    string json = File.ReadAllText(manifestEntry.Abs);
                    manifest = JsonSerializer.Deserialize<SpManifest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new SpManifest();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: could not parse {ManifestName} ({ex.Message}) - continuing without SharePoint cross-references.");
                }
            }

            Console.WriteLine($"Discovered {contentFiles.Count} file(s) in {outputFolder}");

            var nodes = contentFiles.Select(f => new FileNode
            {
                Abs = f.Abs,
                Rel = f.Rel,
                Kind = Classify(f.Rel),
                HtmlRel = HtmlRelFor(f.Rel)
            }).ToList();

            // -----------------------------------------------------------------
            // 2. GROUPING - recognized "<Type>Doc[-] <name>" folder convention, generic
            // fallback for anything not in the tier map so a future documenter type
            // still gets a coherent (if unlabelled-by-us) group instead of disappearing.
            // -----------------------------------------------------------------
            var byTopSegment = new Dictionary<string, List<FileNode>>();
            foreach (FileNode n in nodes)
            {
                string[] segments = n.Rel.Split('/');
                string key = segments.Length > 1 ? segments[0] : "__root__";
                if (!byTopSegment.TryGetValue(key, out var list)) byTopSegment[key] = list = new List<FileNode>();
                list.Add(n);
            }

            var groups = new List<Group>();
            var rootDocs = new List<RootDoc>();
            Group FindOrCreateGroup(string id, string type, string label, int order)
            {
                Group g = groups.FirstOrDefault(x => x.Id == id);
                if (g == null) { g = new Group { Id = id, Type = type, Label = label, Order = order }; groups.Add(g); }
                return g;
            }

            var docFolderRe = new Regex("^([A-Za-z]+)Doc\\s*-?\\s*(.+)$");

            foreach (var (key, filesInGroup) in byTopSegment)
            {
                if (key == "__root__")
                {
                    foreach (FileNode n in filesInGroup)
                    {
                        rootDocs.Add(new RootDoc
                        {
                            Id = "root-" + MarkdownRenderer.Slugify(n.Rel),
                            Label = Humanize(Path.GetFileName(n.Rel)),
                            Href = n.HtmlRel,
                            Node = n
                        });
                    }
                    continue;
                }

                Match m = docFolderRe.Match(key);
                string docTypeRaw = m.Success ? m.Groups[1].Value : "other";
                string docName = m.Success ? m.Groups[2].Value : key;
                var (tierId, tierType, tierLabel, tierOrder) = TierFor(m.Success ? docTypeRaw : "other");
                Group group = FindOrCreateGroup(tierId, tierType, tierLabel, tierOrder);

                // Split by recognized aggregate-file prefix, not by folder depth: real
                // conventions differ (Flow puts per-action files in an actions/ subfolder;
                // App puts per-screen files flat, alongside its own aggregate files), so
                // depth alone can't tell "overview tab" from "item detail" apart.
                var tabCandidates = new List<(FileNode Node, string Prefix)>();
                var kidCandidates = new List<FileNode>();
                foreach (FileNode n in filesInGroup)
                {
                    string baseName = Regex.Replace(Path.GetFileName(n.Rel), @"\.[a-z0-9]+$", "", RegexOptions.IgnoreCase);
                    int dash = baseName.IndexOf('-');
                    string prefix = (dash > 0 ? baseName.Substring(0, dash) : baseName).ToLowerInvariant();
                    if (TabLabels.ContainsKey(prefix)) tabCandidates.Add((n, prefix));
                    else kidCandidates.Add(n);
                }
                tabCandidates = tabCandidates.OrderBy(t => Array.IndexOf(TabPrefixOrder, t.Prefix)).ToList();

                FileNode entryNode = tabCandidates.FirstOrDefault(t => t.Prefix == "index").Node
                    ?? tabCandidates.FirstOrDefault().Node
                    ?? kidCandidates.FirstOrDefault();

                List<NavTabDto> tabs = null;
                var tabIdByNodeRel = new Dictionary<string, string>();
                if (tabCandidates.Count > 1)
                {
                    tabs = tabCandidates.Select(t =>
                    {
                        tabIdByNodeRel[t.Node.Rel] = t.Prefix;
                        return new NavTabDto { Id = t.Prefix, Label = TabLabels[t.Prefix], File = Path.GetFileName(t.Node.HtmlRel) };
                    }).ToList();
                }

                string StripDocNameSuffix(string stem)
                {
                    string safeDocName = Regex.Replace(docName, "[^a-zA-Z0-9]+", "-").Trim('-');
                    if (string.IsNullOrEmpty(safeDocName)) return stem;
                    string pattern = "[-_]*" + Regex.Escape(safeDocName) + "$";
                    return Regex.Replace(stem, pattern, "", RegexOptions.IgnoreCase);
                }
                string KidLabelFor(FileNode n)
                {
                    string[] withinFolder = n.Rel.Split('/').Skip(1).ToArray();
                    string[] dirPart = withinFolder.Take(withinFolder.Length - 1).ToArray();
                    string stem = Regex.Replace(withinFolder[^1], @"\.[a-z0-9]+$", "", RegexOptions.IgnoreCase);
                    stem = Regex.Replace(stem, @"\([^)]*\)\s*$", "");
                    stem = StripDocNameSuffix(stem);
                    stem = Regex.Replace(stem, "^(screen|action)-", "", RegexOptions.IgnoreCase);
                    string label = Humanize(stem);
                    if (string.IsNullOrEmpty(label)) label = Humanize(withinFolder[^1]);
                    return dirPart.Length > 0 ? string.Join(" / ", dirPart.Select(Humanize)) + " / " + label : label;
                }

                var kids = kidCandidates
                    .OrderBy(n => n.Rel, StringComparer.Ordinal)
                    .Select(n => (new NavKidDto { Id = "kid-" + MarkdownRenderer.Slugify(n.Rel), Label = KidLabelFor(n), Href = n.HtmlRel }, n))
                    .ToList();

                var doc = new DocEntry
                {
                    Id = "doc-" + MarkdownRenderer.Slugify(key),
                    Label = docName,
                    Href = entryNode.HtmlRel,
                    Meta = kids.Count > 0 ? $"{kids.Count} item{(kids.Count == 1 ? "" : "s")}" : null,
                    Tabs = tabs,
                    Kids = kids,
                    EntryNode = entryNode,
                    AllNodesInDoc = filesInGroup,
                    TabIdByNodeRel = tabIdByNodeRel
                };
                group.Docs.Add(doc);
            }

            groups = groups.OrderBy(g => g.Order).ToList();
            var solutionGroup = new Group { Id = "solution", Type = "sol", Label = "Solution", Order = 0 };
            solutionGroup.Docs.Add(new DocEntry { Id = "sol-overview", Label = "Overview", Href = "index.html", AllNodesInDoc = new List<FileNode>() });
            // root docs are represented directly via rootDocs, not as DocEntry with a real node search - handled specially below.
            var allGroups = new List<Group> { solutionGroup };
            allGroups.AddRange(groups);

            // -----------------------------------------------------------------
            // 3. SharePoint cross-references (from the manifest, computed here -
            // never injected into any file PowerDocu/the enricher wrote).
            // -----------------------------------------------------------------
            var refsBySource = new Dictionary<string, List<SpReference>>();
            foreach (SpReference r in manifest.References ?? new List<SpReference>())
            {
                string key = r.SourceType + "::" + r.SourceName;
                if (!refsBySource.TryGetValue(key, out var list)) refsBySource[key] = list = new List<SpReference>();
                list.Add(r);
            }
            string CalloutFor(string sourceType, string docName)
            {
                string key = sourceType + "::" + docName;
                if (!refsBySource.TryGetValue(key, out var refs) || refs.Count == 0) return "";
                var items = refs.Select(r =>
                    $"<li>{MarkdownRenderer.Esc(r.ListTitle ?? r.ListIdOrName)} <span style=\"color:var(--text-faint)\">({MarkdownRenderer.Esc(r.SiteUrl)})</span>" +
                    (string.IsNullOrEmpty(r.ActionOrDataSourceName) ? "" : " — via " + MarkdownRenderer.Esc(r.ActionOrDataSourceName)) + "</li>");
                return $"<div class=\"callout\"><h4>References SharePoint</h4><ul>{string.Join("", items)}</ul></div>";
            }

            // -----------------------------------------------------------------
            // 4. Emit files
            // -----------------------------------------------------------------
            if (Directory.Exists(shellFolder)) Directory.Delete(shellFolder, true);
            Directory.CreateDirectory(shellFolder);
            Directory.CreateDirectory(Path.Combine(shellFolder, AssetDirName));

            string exeDir = AppContext.BaseDirectory;
            File.Copy(Path.Combine(exeDir, AssetDirName, "style.css"), Path.Combine(shellFolder, AssetDirName, "style.css"), true);
            File.Copy(Path.Combine(exeDir, AssetDirName, "nav.js"), Path.Combine(shellFolder, AssetDirName, "nav.js"), true);

            string solutionName = Path.GetFileName(outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var navTree = new NavTreeDto();
            foreach (Group g in allGroups)
            {
                var gd = new NavGroupDto { Id = g.Id, Type = g.Type, Label = g.Label, Count = g.Id == "solution" ? null : g.Docs.Count };
                foreach (DocEntry d in g.Docs)
                {
                    gd.Docs.Add(new NavDocDto { Id = d.Id, Label = d.Label, Href = d.Href, Meta = d.Meta, Tabs = d.Tabs, Kids = d.Kids.Count > 0 ? d.Kids.Select(k => k.Kid).ToList() : null });
                }
                if (g.Id == "solution")
                {
                    foreach (RootDoc rd in rootDocs) gd.Docs.Add(new NavDocDto { Id = rd.Id, Label = rd.Label, Href = rd.Href });
                }
                navTree.Groups.Add(gd);
            }

            var searchIndex = new List<SearchEntryDto>();
            foreach (Group g in allGroups)
            {
                foreach (DocEntry d in g.Docs)
                {
                    searchIndex.Add(new SearchEntryDto { N = d.Label, P = g.Label, H = d.Href });
                    foreach (var (kid, _) in d.Kids) searchIndex.Add(new SearchEntryDto { N = kid.Label, P = g.Label + " · " + d.Label, H = kid.Href });
                }
            }

            var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var navDataJs = new StringBuilder();
            navDataJs.Append("window.NAV_TREE = ").Append(JsonSerializer.Serialize(navTree, jsonOpts)).Append(";\n");
            navDataJs.Append("window.SEARCH_INDEX = ").Append(JsonSerializer.Serialize(searchIndex, jsonOpts)).Append(";\n");
            navDataJs.Append("window.SITE_TITLE = ").Append(JsonSerializer.Serialize(solutionName)).Append(";\n");
            File.WriteAllText(Path.Combine(shellFolder, AssetDirName, "nav-data.js"), navDataJs.ToString());

            string PageShell(string title, string rel, object page, string body, bool hasToc)
            {
                string prefix = AssetPrefix(rel);
                string pageJson = JsonSerializer.Serialize(page, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{MarkdownRenderer.Esc(title)}</title>
<link rel=""stylesheet"" href=""{prefix}{AssetDirName}/style.css"">
</head>
<body data-depth=""{DepthOf(rel)}"">
<div class=""app"">
  <header class=""topbar"">
    <a class=""brand"" href=""{prefix}index.html"">
      <div class=""brand-mark"">SD</div>
      <div>
        <div class=""brand-name"">{MarkdownRenderer.Esc(solutionName)}</div>
        <div class=""brand-sub"">generated documentation</div>
      </div>
    </a>
    <nav class=""crumbs"" id=""crumbs""></nav>
    <div class=""top-actions"">
      <div class=""search-wrap"">
        <svg class=""search-icon"" viewBox=""0 0 16 16"" fill=""none"" stroke=""currentColor"" stroke-width=""1.6""><circle cx=""7"" cy=""7"" r=""4.5""/><path d=""M10.5 10.5 14 14""/></svg>
        <input class=""search"" id=""search"" type=""text"" placeholder=""Search…"" autocomplete=""off"">
        <div class=""results"" id=""results""></div>
      </div>
      <button class=""icon-btn"" id=""themeBtn"" title=""Toggle theme"">
        <svg viewBox=""0 0 16 16"" fill=""none"" stroke=""currentColor"" stroke-width=""1.5""><path d=""M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5Z""/></svg>
      </button>
    </div>
  </header>
  <div id=""tabbar""></div>
  <nav class=""sidebar"" id=""sidebar""></nav>
  <main class=""main"" id=""main"">
    <div class=""doc{(hasToc ? "" : " no-toc")}"">
      <article id=""article"">
{body}
      </article>
      {(hasToc ? "<aside class=\"toc\"><div class=\"toc-title\">On this page</div><div id=\"tocBody\"></div></aside>" : "")}
    </div>
  </main>
</div>
<script src=""{prefix}{AssetDirName}/nav-data.js""></script>
<script>window.PAGE = {pageJson};</script>
<script src=""{prefix}{AssetDirName}/nav.js""></script>
</body>
</html>
";
            }

            void WriteFile(string rel, string content)
            {
                string abs = Path.Combine(shellFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllText(abs, content);
            }

            string OriginalLinkFor(string rel)
            {
                string url = "file:///" + outputFolder.Replace('\\', '/').TrimEnd('/') + "/" + Uri.EscapeDataString(rel).Replace("%2F", "/");
                return $"<p class=\"source-note real\"><a href=\"{url}\">View original output file</a> (unmodified, exactly as PowerDocu/the enricher wrote it)</p>";
            }

            // map every node in the Flow/App groups to (sourceType, docLabel) for cross-ref callouts
            var sourceTypeForNode = new Dictionary<string, (string SourceType, string DocName)>();
            foreach (Group g in groups)
            {
                string sourceType = g.Id == "flow" ? "Flow" : g.Id == "app" ? "App" : null;
                if (sourceType == null) continue;
                foreach (DocEntry d in g.Docs)
                    foreach (FileNode n in d.AllNodesInDoc)
                        sourceTypeForNode[n.Rel] = (sourceType, d.Label);
            }

            foreach (FileNode n in nodes)
            {
                string title = Humanize(Path.GetFileName(n.Rel));
                string bodyHtml;
                int headingCount = 0;

                if (n.Kind == "md")
                {
                    string md = ReadTextStripBom(n.Abs);
                    var (renderedHtml, hc) = MarkdownRenderer.ToHtml(md);
                    bodyHtml = renderedHtml;
                    headingCount = hc;
                    if (sourceTypeForNode.TryGetValue(n.Rel, out var src))
                        bodyHtml = CalloutFor(src.SourceType, src.DocName) + bodyHtml;

                    // copy sibling images this markdown references, so inline <img> tags resolve
                    foreach (Match im in Regex.Matches(md, @"!\[[^\]]*\]\(([^)]+)\)"))
                    {
                        string src2 = im.Groups[1].Value;
                        if (Regex.IsMatch(src2, "^https?://", RegexOptions.IgnoreCase)) continue;
                        string srcAbs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(n.Abs)!, src2));
                        if (!File.Exists(srcAbs)) continue;
                        string destAbs = Path.Combine(shellFolder, Path.GetDirectoryName(n.Rel.Replace('/', Path.DirectorySeparatorChar)) ?? "", src2);
                        if (!File.Exists(destAbs))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                            File.Copy(srcAbs, destAbs, true);
                        }
                    }
                }
                else if (n.Kind == "image")
                {
                    string destAbs = Path.Combine(shellFolder, n.Rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                    File.Copy(n.Abs, destAbs, true);
                    bodyHtml = $"<h1>{MarkdownRenderer.Esc(title)}</h1><img src=\"{MarkdownRenderer.Esc(Path.GetFileName(n.Rel))}\" alt=\"{MarkdownRenderer.Esc(title)}\">";
                }
                else
                {
                    string destAbs = Path.Combine(shellFolder, n.Rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                    File.Copy(n.Abs, destAbs, true);
                    string fname = Path.GetFileName(n.Rel);
                    bodyHtml = $"<h1>{MarkdownRenderer.Esc(title)}</h1><div class=\"callout\"><h4>Download</h4><p><a href=\"{MarkdownRenderer.Esc(fname)}\">{MarkdownRenderer.Esc(fname)}</a></p></div>";
                }

                // resolve page metadata: which doc (if any) owns this node
                DocEntry ownerDoc = null;
                Group ownerGroup = null;
                foreach (Group g in groups)
                {
                    DocEntry d = g.Docs.FirstOrDefault(dd => dd.AllNodesInDoc.Any(x => x.Rel == n.Rel));
                    if (d != null) { ownerDoc = d; ownerGroup = g; break; }
                }

                object page;
                if (ownerDoc != null)
                {
                    var kidMatch = ownerDoc.Kids.FirstOrDefault(k => k.Node.Rel == n.Rel);
                    var pageDict = new Dictionary<string, object> { ["group"] = ownerGroup.Id, ["doc"] = ownerDoc.Id };
                    if (ownerDoc.TabIdByNodeRel.TryGetValue(n.Rel, out string tabId)) pageDict["tab"] = tabId;
                    if (kidMatch.Kid != null) pageDict["screenLabel"] = kidMatch.Kid.Label;
                    page = pageDict;
                }
                else
                {
                    RootDoc rootDoc = rootDocs.FirstOrDefault(rd => rd.Node.Rel == n.Rel);
                    page = new Dictionary<string, object>
                    {
                        ["group"] = "solution",
                        ["doc"] = rootDoc?.Id ?? ("root-" + MarkdownRenderer.Slugify(n.Rel)),
                        ["title"] = rootDoc?.Label ?? title
                    };
                }

                string html = PageShell(title, n.HtmlRel, page, bodyHtml + OriginalLinkFor(n.Rel), headingCount > 0);
                WriteFile(n.HtmlRel, html);
            }

            // -----------------------------------------------------------------
            // 5. Home page (dashboard)
            // -----------------------------------------------------------------
            var tierCards = new StringBuilder();
            foreach (Group g in groups)
            {
                string href = g.Docs.FirstOrDefault()?.Href ?? "#";
                tierCards.Append($"<a class=\"stat\" data-t=\"{MarkdownRenderer.Esc(g.Type)}\" href=\"{MarkdownRenderer.Esc(href)}\"><div class=\"stat-n\">{g.Docs.Count}</div><div class=\"stat-l\">{MarkdownRenderer.Esc(g.Label)}</div></a>");
            }
            string rootList = rootDocs.Count > 0
                ? "<ul>" + string.Join("", rootDocs.Select(rd => $"<li><a href=\"{MarkdownRenderer.Esc(rd.Href)}\">{MarkdownRenderer.Esc(rd.Label)}</a></li>")) + "</ul>"
                : "<p class=\"empty\">No solution-level files were found at the root of the output folder.</p>";

            string homeBody = $@"
<h1>{MarkdownRenderer.Esc(solutionName)}</h1>
<p class=""subtitle"">Generated documentation shell. Everything PowerDocu and the SharePoint enricher produced is included below — recognized component types get an organized view; anything else still appears, grouped by where it sits on disk.</p>
<div class=""stats"">{tierCards}</div>
<h2 id=""root-files"">Solution-level files</h2>
{rootList}
";
            string homePage = PageShell(solutionName, "index.html", new Dictionary<string, object> { ["group"] = "solution", ["doc"] = "sol-overview" }, homeBody, true);
            WriteFile("index.html", homePage);

            Console.WriteLine($"Wrote shell to {shellFolder} ({nodes.Count + 1} page(s)).");
            return 0;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        private static List<(string Abs, string Rel)> Discover(string dir, string baseDir, string shellFolder)
        {
            var result = new List<(string, string)>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Path.GetFullPath(entry) == shellFolder) continue; // never fold our own prior output back in
                if (Directory.Exists(entry))
                {
                    result.AddRange(Discover(entry, baseDir, shellFolder));
                }
                else
                {
                    string rel = Path.GetRelativePath(baseDir, entry).Replace(Path.DirectorySeparatorChar, '/');
                    result.Add((entry, rel));
                }
            }
            return result;
        }

        private static string ReadTextStripBom(string path)
        {
            // Grynwald.MarkdownGenerator (PowerDocu's Markdown writer) prefixes every
            // file with a UTF-8 BOM; left in place, it silently breaks the "line starts
            // with #" heading match on every file's very first line.
            string text = File.ReadAllText(path, Encoding.UTF8);
            return text.TrimStart('﻿');
        }

        private static string Humanize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string t = Regex.Replace(s, @"\.[a-z0-9]+$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "[-_]+", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            t = Regex.Replace(t, @"\b\w", m => m.Value.ToUpperInvariant());
            return string.IsNullOrEmpty(t) ? s : t;
        }

        private static string ExtOf(string rel)
        {
            Match m = Regex.Match(rel, @"\.[a-z0-9]+$", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }

        private static string Classify(string rel)
        {
            string ext = ExtOf(rel);
            if (ext == ".md") return "md";
            if (ImageExt.Contains(ext)) return "image";
            return "other";
        }

        private static int DepthOf(string rel) => rel.Split('/').Length - 1;

        private static string AssetPrefix(string rel) => string.Concat(Enumerable.Repeat("../", DepthOf(rel)));

        private static string HtmlRelFor(string rel)
        {
            // .md -> replace extension with .html; everything else -> append .html
            // (the original asset is copied unchanged alongside, at its own `rel`).
            return Classify(rel) == "md" ? Regex.Replace(rel, @"\.md$", ".html", RegexOptions.IgnoreCase) : rel + ".html";
        }

        private static (string Id, string Type, string Label, int Order) TierFor(string docTypeRaw)
        {
            string key = docTypeRaw.ToLowerInvariant();
            foreach (var (tKey, tType, tLabel, tOrder) in KnownTiers)
                if (tKey == key) return (key, tType, tLabel, tOrder);
            string label = Humanize(docTypeRaw) + (docTypeRaw.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? "" : "s");
            return (key, key, label, 500);
        }
    }
}
