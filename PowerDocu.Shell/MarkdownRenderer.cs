using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerDocu.Shell
{
    /// <summary>
    /// Deliberately not a full CommonMark implementation - good enough for PowerDocu's
    /// own consistently-generated Markdown (headings, tables, lists, links, images,
    /// bold/italic, inline/fenced code, blockquotes). Anything it renders oddly is
    /// still 100% present as text - never dropped. Direct C# port of the original
    /// Shell/build.js prototype's mdToHtml/inline functions - see that file's header
    /// comment for the design rationale (guaranteed-inclusion floor).
    /// </summary>
    public static class MarkdownRenderer
    {
        public static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static string Slugify(string s)
        {
            string result = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(result) ? "section" : result;
        }

        private static string Inline(string text)
        {
            string t = Esc(text);
            t = Regex.Replace(t, "`([^`]+)`", "<code>$1</code>");
            // Greedy to the LAST ')' on the line, not the first: real PowerDocu filenames
            // often embed a "(guid)" segment, which a naive [^)]+ capture truncates at.
            t = Regex.Replace(t, @"!\[([^\]]*)\]\((.+)\)", m => $"<img src=\"{Esc(m.Groups[2].Value)}\" alt=\"{Esc(m.Groups[1].Value)}\">");
            t = Regex.Replace(t, @"\[([^\]]+)\]\((.+)\)", m =>
            {
                string href = m.Groups[2].Value;
                // Internal cross-links point at sibling .md files; we render everything
                // to .html, so rewrite those (leave external/absolute links untouched).
                string rewritten = Regex.IsMatch(href, "^https?://", RegexOptions.IgnoreCase)
                    ? href
                    : Regex.Replace(href, @"\.md(#.*)?$", ".html$1", RegexOptions.IgnoreCase);
                return $"<a href=\"{Esc(rewritten)}\">{m.Groups[1].Value}</a>";
            });
            t = Regex.Replace(t, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
            t = Regex.Replace(t, @"(?:^|[^*])\*([^*]+)\*(?!\*)", m => Regex.Replace(m.Value, @"\*([^*]+)\*", "<em>$1</em>"));
            // backslash-escaped literals (Grynwald.MarkdownGenerator escapes these pervasively)
            t = Regex.Replace(t, @"\\([\\`*_{}\[\]()#+\-.!])", "$1");
            return t;
        }

        public static (string Html, int HeadingCount) ToHtml(string md)
        {
            string[] lines = md.Replace("\r\n", "\n").Split('\n');
            var outLines = new List<string>();
            int i = 0;
            int headingCount = 0;

            while (i < lines.Length)
            {
                string line = lines[i];

                if (Regex.IsMatch(line, "^```"))
                {
                    var buf = new List<string>();
                    i++;
                    while (i < lines.Length && !Regex.IsMatch(lines[i], "^```")) { buf.Add(lines[i]); i++; }
                    i++; // skip closing fence
                    outLines.Add("<pre><code>" + Esc(string.Join("\n", buf)) + "</code></pre>");
                    continue;
                }

                Match heading = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                if (heading.Success)
                {
                    int level = Math.Min(heading.Groups[1].Value.Length, 3); // fold h4-h6 into h3 for TOC simplicity
                    string text = heading.Groups[2].Value.Trim();
                    string id = (level == 2 || level == 3) ? $" id=\"{Slugify(text)}\"" : "";
                    if (level == 2 || level == 3) headingCount++;
                    outLines.Add($"<h{level}{id}>{Inline(text)}</h{level}>");
                    i++;
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*\|.*\|\s*$") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?[\s:|-]+\|?\s*$"))
                {
                    string[] headerCells = Trim(line).Split('|');
                    for (int c = 0; c < headerCells.Length; c++) headerCells[c] = headerCells[c].Trim();
                    i += 2;
                    var rows = new List<string[]>();
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\|.*\|\s*$"))
                    {
                        string[] cells = Trim(lines[i]).Split('|');
                        for (int c = 0; c < cells.Length; c++) cells[c] = cells[c].Trim();
                        rows.Add(cells);
                        i++;
                    }
                    var sb = new StringBuilder();
                    sb.Append("<table><thead><tr>");
                    foreach (string c in headerCells) sb.Append($"<th>{Inline(c)}</th>");
                    sb.Append("</tr></thead><tbody>");
                    foreach (string[] r in rows)
                    {
                        sb.Append("<tr>");
                        foreach (string c in r) sb.Append($"<td>{Inline(c)}</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table>");
                    outLines.Add(sb.ToString());
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*([-*]|\d+\.)\s+"))
                {
                    bool ordered = Regex.IsMatch(line, @"^\s*\d+\.");
                    string tag = ordered ? "ol" : "ul";
                    var items = new List<string>();
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*([-*]|\d+\.)\s+"))
                    {
                        items.Add(Regex.Replace(lines[i], @"^\s*([-*]|\d+\.)\s+", ""));
                        i++;
                    }
                    var sb = new StringBuilder();
                    sb.Append('<').Append(tag).Append('>');
                    foreach (string it in items) sb.Append($"<li>{Inline(it)}</li>");
                    sb.Append("</").Append(tag).Append('>');
                    outLines.Add(sb.ToString());
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*>"))
                {
                    var items = new List<string>();
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*>"))
                    {
                        items.Add(Regex.Replace(lines[i], @"^\s*>\s?", ""));
                        i++;
                    }
                    var rendered = new List<string>();
                    foreach (string it in items) rendered.Add(Inline(it));
                    outLines.Add("<blockquote>" + string.Join("<br>", rendered) + "</blockquote>");
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*(---+|\*\*\*+)\s*$")) { outLines.Add("<hr>"); i++; continue; }

                if (Regex.IsMatch(line, @"^\s*$")) { i++; continue; }

                // paragraph: consume until a blank line or a line starting a new block
                var pbuf = new List<string> { line };
                i++;
                while (i < lines.Length
                    && !Regex.IsMatch(lines[i], @"^\s*$")
                    && !Regex.IsMatch(lines[i], "^(#{1,6})\\s+")
                    && !Regex.IsMatch(lines[i], @"^\s*([-*]|\d+\.)\s+")
                    && !Regex.IsMatch(lines[i], "^```"))
                {
                    pbuf.Add(lines[i]);
                    i++;
                }
                var pRendered = new List<string>();
                foreach (string p in pbuf) pRendered.Add(Inline(p));
                outLines.Add("<p>" + string.Join("<br>", pRendered) + "</p>");
            }

            return (string.Join("\n", outLines), headingCount);
        }

        private static string Trim(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t;
        }
    }
}
