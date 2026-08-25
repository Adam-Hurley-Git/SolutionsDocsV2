#!/usr/bin/env node
/*
 * Solutions Docs v2 - reshell tool.
 *
 * Reads a PowerDocu output folder (and whatever the SharePoint enricher added
 * alongside it - same folder, new files only) and builds a navigable/searchable
 * HTML shell in a separate output folder. Read-only on its input: never opens
 * a file in outputFolder for writing.
 *
 * Design principle (see PROGRESS-LOG.md / HANDOFF.md "reshell" discussion):
 * inclusion is a total function, not a pattern match. Every single file found
 * gets a page, by a render rule that has no "unrecognized" branch (md -> html,
 * image -> embed, anything else -> download link) and a nav position that
 * defaults to mirroring its folder path. A second, best-effort pass then
 * upgrades whatever it recognizes (FlowDoc/AppDoc/etc. naming, tabs, TOC,
 * SharePoint cross-references) - upgrades only, never a gate on inclusion.
 *
 * Usage: node build.js <powerdocuOutputFolder> [shellOutputFolder]
 *   shellOutputFolder defaults to <powerdocuOutputFolder>/Shell
 */
'use strict';
const fs = require('fs');
const path = require('path');

const ASSET_DIR_NAME = 'assets';
const MANIFEST_NAME = 'sharepoint-references.json';

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------
const outputFolderArg = process.argv[2];
if (!outputFolderArg) {
  console.error('Usage: node build.js <powerdocuOutputFolder> [shellOutputFolder]');
  process.exit(1);
}
const outputFolder = path.resolve(outputFolderArg);
if (!fs.existsSync(outputFolder) || !fs.statSync(outputFolder).isDirectory()) {
  console.error('Output folder not found: ' + outputFolder);
  process.exit(1);
}
const shellFolder = path.resolve(process.argv[3] || path.join(outputFolder, 'Shell'));

// ---------------------------------------------------------------------------
// 1. DISCOVERY - guaranteed-inclusion floor. Pure enumeration, no interpretation.
// ---------------------------------------------------------------------------
function discover(dir, base) {
  let out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const abs = path.join(dir, entry.name);
    if (path.resolve(abs) === shellFolder) continue; // never fold our own prior output back in
    if (entry.isDirectory()) {
      out = out.concat(discover(abs, base));
    } else {
      out.push({ abs, rel: path.relative(base, abs).split(path.sep).join('/') });
    }
  }
  return out;
}

const allFiles = discover(outputFolder, outputFolder);
const manifestFile = allFiles.find(f => f.rel === MANIFEST_NAME);
const contentFiles = allFiles.filter(f => f.rel !== MANIFEST_NAME);

let manifest = { sites: [], references: [] };
if (manifestFile) {
  try {
    manifest = JSON.parse(fs.readFileSync(manifestFile.abs, 'utf8'));
  } catch (e) {
    console.error('Warning: could not parse ' + MANIFEST_NAME + ' (' + e.message + ') - continuing without SharePoint cross-references.');
  }
}

console.log('Discovered ' + contentFiles.length + ' file(s) in ' + outputFolder);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
function readText(abs) {
  // Grynwald.MarkdownGenerator (PowerDocu's Markdown writer) prefixes every
  // file with a UTF-8 BOM; left in place, it silently breaks the "line starts
  // with #" heading match on every file's very first line.
  return fs.readFileSync(abs, 'utf8').replace(/^﻿/, '');
}
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function slugify(s) {
  return String(s).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'section';
}
function humanize(s) {
  return String(s)
    .replace(/\.[a-z0-9]+$/i, '')
    .replace(/[-_]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/\b\w/g, c => c.toUpperCase()) || s;
}
function extOf(rel) {
  const m = /\.[a-z0-9]+$/i.exec(rel);
  return m ? m[0].toLowerCase() : '';
}
const IMAGE_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.bmp']);
const SVG_EXT = '.svg';
function classify(rel) {
  const ext = extOf(rel);
  if (ext === '.md') return 'md';
  if (IMAGE_EXT.has(ext) || ext === SVG_EXT) return 'image';
  return 'other';
}
function depthOf(rel) {
  return rel.split('/').length - 1;
}
function assetPrefix(rel) {
  return '../'.repeat(depthOf(rel));
}
function htmlRelFor(rel) {
  // .md -> replace extension with .html; everything else -> append .html
  // (the original asset is copied unchanged alongside, at its own `rel`).
  return classify(rel) === 'md' ? rel.replace(/\.md$/i, '.html') : rel + '.html';
}

// ---------------------------------------------------------------------------
// 2. MARKDOWN -> HTML - deliberately not a full CommonMark implementation.
// Good enough for PowerDocu's own consistently-generated Markdown (headings,
// tables, lists, links, images, bold/italic, inline/fenced code, blockquotes).
// Anything it renders oddly is still 100% present as text - never dropped.
// ---------------------------------------------------------------------------
function inline(text) {
  let t = esc(text);
  t = t.replace(/`([^`]+)`/g, '<code>$1</code>');
  // Greedy to the LAST ')' on the line, not the first: real PowerDocu filenames
  // often embed a "(guid)" segment, which a naive [^)]+ capture truncates at.
  t = t.replace(/!\[([^\]]*)\]\((.+)\)/g, (m, alt, src) => `<img src="${esc(src)}" alt="${esc(alt)}">`);
  t = t.replace(/\[([^\]]+)\]\((.+)\)/g, (m, txt, href) => {
    // Internal cross-links point at sibling .md files; we render everything
    // to .html, so rewrite those (leave external/absolute links untouched).
    const rewritten = /^https?:\/\//i.test(href) ? href : href.replace(/\.md(#.*)?$/i, '.html$1');
    return `<a href="${esc(rewritten)}">${txt}</a>`;
  });
  t = t.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  t = t.replace(/(?:^|[^*])\*([^*]+)\*(?!\*)/g, m => m.replace(/\*([^*]+)\*/, '<em>$1</em>'));
  t = t.replace(/\\([\\`*_{}\[\]()#+\-.!])/g, '$1'); // backslash-escaped literals (Grynwald.MarkdownGenerator escapes these pervasively)
  return t;
}
function mdToHtml(md) {
  const lines = md.replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let i = 0;
  let headingCount = 0;
  while (i < lines.length) {
    const line = lines[i];

    if (/^```/.test(line)) {
      const buf = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) { buf.push(lines[i]); i++; }
      i++; // skip closing fence
      out.push('<pre><code>' + esc(buf.join('\n')) + '</code></pre>');
      continue;
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      const level = Math.min(heading[1].length, 3); // fold h4-h6 into h3 for TOC simplicity
      const text = heading[2].trim();
      const id = (level === 2 || level === 3) ? ` id="${slugify(text)}"` : '';
      if (level === 2 || level === 3) headingCount++;
      out.push(`<h${level}${id}>${inline(text)}</h${level}>`);
      i++;
      continue;
    }

    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|?[\s:|-]+\|?\s*$/.test(lines[i + 1])) {
      const headerCells = line.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
      i += 2;
      const rows = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) {
        rows.push(lines[i].trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim()));
        i++;
      }
      let t = '<table><thead><tr>' + headerCells.map(c => `<th>${inline(c)}</th>`).join('') + '</tr></thead><tbody>';
      for (const r of rows) t += '<tr>' + r.map(c => `<td>${inline(c)}</td>`).join('') + '</tr>';
      t += '</tbody></table>';
      out.push(t);
      continue;
    }

    if (/^\s*([-*]|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      const tag = ordered ? 'ol' : 'ul';
      const items = [];
      while (i < lines.length && /^\s*([-*]|\d+\.)\s+/.test(lines[i])) {
        items.push(lines[i].replace(/^\s*([-*]|\d+\.)\s+/, ''));
        i++;
      }
      out.push(`<${tag}>` + items.map(it => `<li>${inline(it)}</li>`).join('') + `</${tag}>`);
      continue;
    }

    if (/^\s*>/.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) { items.push(lines[i].replace(/^\s*>\s?/, '')); i++; }
      out.push('<blockquote>' + items.map(inline).join('<br>') + '</blockquote>');
      continue;
    }

    if (/^\s*(---+|\*\*\*+)\s*$/.test(line)) { out.push('<hr>'); i++; continue; }

    if (/^\s*$/.test(line)) { i++; continue; }

    // paragraph: consume until a blank line or a line starting a new block
    const buf = [line];
    i++;
    while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^(#{1,6})\s+/.test(lines[i]) && !/^\s*([-*]|\d+\.)\s+/.test(lines[i]) && !/^```/.test(lines[i])) {
      buf.push(lines[i]);
      i++;
    }
    out.push('<p>' + buf.map(inline).join('<br>') + '</p>');
  }
  return { html: out.join('\n'), headingCount };
}

// ---------------------------------------------------------------------------
// 3. TIER MAP - recognized "<Type>Doc <name>" folder convention, generic
// fallback for anything not in the map so a future documenter type still
// gets a coherent (if unlabelled-by-us) group instead of disappearing.
// ---------------------------------------------------------------------------
const TIER_INFO = {
  flow: { label: 'Flows', type: 'flow', order: 10 },
  app: { label: 'Apps', type: 'app', order: 20 },
  agent: { label: 'Agents', type: 'agent', order: 30 },
  appmodule: { label: 'Model-driven apps', type: 'appmodule', order: 40 },
  bpf: { label: 'Business process flows', type: 'bpf', order: 50 },
  desktopflow: { label: 'Desktop flows', type: 'desktopflow', order: 60 },
  classicworkflow: { label: 'Classic workflows', type: 'classicworkflow', order: 70 },
  aimodel: { label: 'AI models', type: 'aimodel', order: 80 },
  dataflow: { label: 'Dataflows', type: 'dataflow', order: 90 },
  sharepoint: { label: 'SharePoint', type: 'sp', order: 100 }
};
const TAB_LABELS = {
  index: 'Overview', connections: 'Connections', variables: 'Variables',
  triggersactions: 'Trigger & actions', appdetails: 'App details',
  datasources: 'Data sources', resources: 'Resources', screens: 'Screens',
  controls: 'Controls'
};
function tierFor(docTypeRaw) {
  const key = docTypeRaw.toLowerCase();
  if (TIER_INFO[key]) return { id: key, ...TIER_INFO[key] };
  return { id: key, label: humanize(docTypeRaw) + (/s$/.test(docTypeRaw) ? '' : 's'), type: key, order: 500 };
}

// ---------------------------------------------------------------------------
// 4. Build the node list (one per discovered file) + group them.
// ---------------------------------------------------------------------------
const DOC_FOLDER_RE = /^([A-Za-z]+)Doc\s*-?\s*(.+)$/;

const nodes = contentFiles.map(f => ({
  rel: f.rel,
  abs: f.abs,
  kind: classify(f.rel),
  htmlRel: htmlRelFor(f.rel)
}));

// group by top-level path segment
const byTopSegment = new Map();
for (const n of nodes) {
  const segments = n.rel.split('/');
  const top = segments.length > 1 ? segments[0] : null;
  const key = top || '__root__';
  if (!byTopSegment.has(key)) byTopSegment.set(key, []);
  byTopSegment.get(key).push(n);
}

const groups = []; // { id, type, label, order, docs: [...] }
const rootDocs = []; // standalone root-level files -> each its own doc entry

function findOrCreateGroup(id, info) {
  let g = groups.find(x => x.id === id);
  if (!g) { g = { id, type: info.type, label: info.label, order: info.order, docs: [] }; groups.push(g); }
  return g;
}

for (const [key, filesInGroup] of byTopSegment) {
  if (key === '__root__') {
    for (const n of filesInGroup) {
      rootDocs.push({
        id: 'root-' + slugify(n.rel),
        label: humanize(n.rel.split('/').pop()),
        href: n.htmlRel,
        node: n
      });
    }
    continue;
  }

  const m = DOC_FOLDER_RE.exec(key);
  const docTypeRaw = m ? m[1] : 'other';
  const docName = m ? m[2] : key;
  const tier = tierFor(m ? docTypeRaw : 'other');
  const group = findOrCreateGroup(tier.id, tier);

  // Split by recognized aggregate-file prefix, not by folder depth: real
  // conventions differ (Flow puts per-action files in an actions/ subfolder;
  // App puts per-screen files flat, alongside its own aggregate files), so
  // depth alone can't tell "overview tab" from "item detail" apart. A file
  // whose text-before-the-first-hyphen matches a known aggregate prefix is a
  // tab; everything else in the folder - at any depth - is a detail item.
  // An unrecognized prefix just means "not promoted to a tab", never dropped.
  const tabPrefixOrder = Object.keys(TAB_LABELS);
  const tabCandidates = [];
  const kidCandidates = [];
  for (const n of filesInGroup) {
    const base = path.basename(n.rel).replace(/\.[a-z0-9]+$/i, '');
    const dash = base.indexOf('-');
    const prefix = dash > 0 ? base.slice(0, dash).toLowerCase() : base.toLowerCase();
    if (Object.prototype.hasOwnProperty.call(TAB_LABELS, prefix)) tabCandidates.push({ n, prefix });
    else kidCandidates.push(n);
  }
  tabCandidates.sort((a, b) => tabPrefixOrder.indexOf(a.prefix) - tabPrefixOrder.indexOf(b.prefix));

  let entryNode = (tabCandidates.find(t => t.prefix === 'index') || tabCandidates[0] || { n: kidCandidates[0] }).n;

  let tabs;
  if (tabCandidates.length > 1) {
    tabs = tabCandidates.map(({ n, prefix }) => ({ id: prefix, label: TAB_LABELS[prefix], file: path.basename(n.htmlRel), node: n }));
  }

  function stripDocNameSuffix(stem) {
    const safeDocName = docName.replace(/[^a-z0-9]+/gi, '-').replace(/^-+|-+$/g, '');
    if (!safeDocName) return stem;
    const re = new RegExp('[-_]*' + safeDocName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$', 'i');
    return stem.replace(re, '');
  }
  function kidLabelFor(n) {
    const withinFolder = n.rel.split('/').slice(1);
    const dirPart = withinFolder.slice(0, -1);
    let stem = withinFolder[withinFolder.length - 1].replace(/\.[a-z0-9]+$/i, '');
    stem = stem.replace(/\([^)]*\)\s*$/, '');
    stem = stripDocNameSuffix(stem);
    stem = stem.replace(/^(screen|action)-/i, '');
    const label = humanize(stem) || humanize(withinFolder[withinFolder.length - 1]);
    return dirPart.length ? dirPart.map(humanize).join(' / ') + ' / ' + label : label;
  }

  const kids = kidCandidates
    .sort((a, b) => a.rel.localeCompare(b.rel))
    .map(n => ({ id: 'kid-' + slugify(n.rel), label: kidLabelFor(n), href: n.htmlRel, node: n }));

  group.docs.push({
    id: 'doc-' + slugify(key),
    label: docName,
    href: entryNode.htmlRel,
    meta: kids.length ? `${kids.length} item${kids.length === 1 ? '' : 's'}` : null,
    tabs,
    kids: kids.length ? kids : undefined,
    entryNode,
    allNodesInDoc: filesInGroup
  });
}

groups.sort((a, b) => a.order - b.order);
const solutionGroup = { id: 'solution', type: 'sol', label: 'Solution', order: 0, docs: [{ id: 'sol-overview', label: 'Overview', href: 'index.html' }, ...rootDocs] };
const allGroups = [solutionGroup, ...groups];

// ---------------------------------------------------------------------------
// 5. SharePoint cross-references (from the manifest, computed here - never
// injected into any file PowerDocu/the enricher wrote).
// ---------------------------------------------------------------------------
const refsBySource = new Map(); // "Flow::Name" -> [ref, ...]
for (const r of manifest.references || []) {
  const key = r.sourceType + '::' + r.sourceName;
  if (!refsBySource.has(key)) refsBySource.set(key, []);
  refsBySource.get(key).push(r);
}
function calloutFor(docTypeLabelSourceType, docName) {
  const key = docTypeLabelSourceType + '::' + docName;
  const refs = refsBySource.get(key);
  if (!refs || !refs.length) return '';
  const items = refs.map(r => `<li>${esc(r.listTitle || r.listIdOrName)} <span style="color:var(--text-faint)">(${esc(r.siteUrl)})</span>${r.actionOrDataSourceName ? ' — via ' + esc(r.actionOrDataSourceName) : ''}</li>`).join('');
  return `<div class="callout"><h4>References SharePoint</h4><ul>${items}</ul></div>`;
}

// ---------------------------------------------------------------------------
// 6. Emit files
// ---------------------------------------------------------------------------
fs.rmSync(shellFolder, { recursive: true, force: true });
fs.mkdirSync(shellFolder, { recursive: true });
fs.mkdirSync(path.join(shellFolder, ASSET_DIR_NAME), { recursive: true });

// ship our own chrome assets (style.css / nav.js live beside this script)
fs.copyFileSync(path.join(__dirname, ASSET_DIR_NAME, 'style.css'), path.join(shellFolder, ASSET_DIR_NAME, 'style.css'));
fs.copyFileSync(path.join(__dirname, ASSET_DIR_NAME, 'nav.js'), path.join(shellFolder, ASSET_DIR_NAME, 'nav.js'));

function navTreeJson() {
  const groupsOut = allGroups.map(g => ({
    id: g.id, type: g.type, label: g.label, count: g.id === 'solution' ? undefined : g.docs.length,
    docs: g.docs.map(d => ({
      id: d.id, label: d.label, href: d.href, meta: d.meta || undefined,
      tabs: d.tabs ? d.tabs.map(t => ({ id: t.id, label: t.label, file: t.file })) : undefined,
      kids: d.kids ? d.kids.map(k => ({ id: k.id, label: k.label, href: k.href })) : undefined
    }))
  }));
  return { groups: groupsOut };
}
function searchIndexJson() {
  const idx = [];
  for (const g of allGroups) {
    for (const d of g.docs) {
      idx.push({ n: d.label, p: g.label, h: d.href });
      for (const k of (d.kids || [])) idx.push({ n: k.label, p: g.label + ' · ' + d.label, h: k.href });
    }
  }
  return idx;
}
const solutionName = path.basename(outputFolder);
fs.writeFileSync(path.join(shellFolder, ASSET_DIR_NAME, 'nav-data.js'),
  'window.NAV_TREE = ' + JSON.stringify(navTreeJson(), null, 2) + ';\n' +
  'window.SEARCH_INDEX = ' + JSON.stringify(searchIndexJson(), null, 2) + ';\n' +
  'window.SITE_TITLE = ' + JSON.stringify(solutionName) + ';\n');

function pageShell({ title, rel, page, body, hasToc, hasTabs }) {
  const prefix = assetPrefix(rel);
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>${esc(title)}</title>
<link rel="stylesheet" href="${prefix}${ASSET_DIR_NAME}/style.css">
</head>
<body data-depth="${depthOf(rel)}">
<div class="app">
  <header class="topbar">
    <a class="brand" href="${prefix}index.html">
      <div class="brand-mark">SD</div>
      <div>
        <div class="brand-name">${esc(solutionName)}</div>
        <div class="brand-sub">generated documentation</div>
      </div>
    </a>
    <nav class="crumbs" id="crumbs"></nav>
    <div class="top-actions">
      <div class="search-wrap">
        <svg class="search-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6"><circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5 14 14"/></svg>
        <input class="search" id="search" type="text" placeholder="Search…" autocomplete="off">
        <div class="results" id="results"></div>
      </div>
      <button class="icon-btn" id="themeBtn" title="Toggle theme">
        <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5Z"/></svg>
      </button>
    </div>
  </header>
  <div id="tabbar"></div>
  <nav class="sidebar" id="sidebar"></nav>
  <main class="main" id="main">
    <div class="doc${hasToc ? '' : ' no-toc'}">
      <article id="article">
${body}
      </article>
      ${hasToc ? `<aside class="toc"><div class="toc-title">On this page</div><div id="tocBody"></div></aside>` : ''}
    </div>
  </main>
</div>
<script src="${prefix}${ASSET_DIR_NAME}/nav-data.js"></script>
<script>window.PAGE = ${JSON.stringify(page)};</script>
<script src="${prefix}${ASSET_DIR_NAME}/nav.js"></script>
</body>
</html>
`;
}

function writeFile(rel, content) {
  const abs = path.join(shellFolder, rel);
  fs.mkdirSync(path.dirname(abs), { recursive: true });
  fs.writeFileSync(abs, content);
}

function originalLinkFor(rel) {
  const backToRoot = '../'.repeat(depthOf(rel));
  return `<p class="source-note real"><a href="file:///${outputFolder.replace(/\\/g, '/')}/${encodeURI(rel)}">View original output file</a> (unmodified, exactly as PowerDocu/the enricher wrote it)</p>`;
}

// render every node per the guaranteed floor
let sourceTypeForNode = new Map();
for (const g of groups) {
  const sourceType = g.id === 'flow' ? 'Flow' : g.id === 'app' ? 'App' : null;
  for (const d of g.docs) for (const n of d.allNodesInDoc) sourceTypeForNode.set(n.rel, { sourceType, docName: d.label });
}

for (const n of nodes) {
  const title = humanize(path.basename(n.rel));
  let bodyHtml, headingCount = 0;

  if (n.kind === 'md') {
    const md = readText(n.abs);
    const rendered = mdToHtml(md);
    bodyHtml = rendered.html;
    headingCount = rendered.headingCount;
    const src = sourceTypeForNode.get(n.rel);
    if (src && src.sourceType) bodyHtml = calloutFor(src.sourceType, src.docName) + bodyHtml;
    // copy any relative images this markdown references so inline <img> tags resolve
  } else if (n.kind === 'image') {
    fs.mkdirSync(path.dirname(path.join(shellFolder, n.rel)), { recursive: true });
    fs.copyFileSync(n.abs, path.join(shellFolder, n.rel));
    bodyHtml = `<h1>${esc(title)}</h1><img src="${esc(path.basename(n.rel))}" alt="${esc(title)}">`;
  } else {
    fs.mkdirSync(path.dirname(path.join(shellFolder, n.rel)), { recursive: true });
    fs.copyFileSync(n.abs, path.join(shellFolder, n.rel));
    bodyHtml = `<h1>${esc(title)}</h1><div class="callout"><h4>Download</h4><p><a href="${esc(path.basename(n.rel))}">${esc(path.basename(n.rel))}</a></p></div>`;
  }

  // also copy sibling image assets referenced by markdown, if not already a discovered node's own copy target
  if (n.kind === 'md') {
    const md = readText(n.abs);
    const imgRefs = [...md.matchAll(/!\[[^\]]*\]\(([^)]+)\)/g)].map(m => m[1]).filter(src => !/^https?:\/\//i.test(src));
    for (const src of imgRefs) {
      const srcAbs = path.resolve(path.dirname(n.abs), src);
      const srcRel = path.relative(outputFolder, srcAbs).split(path.sep).join('/');
      const destAbs = path.join(shellFolder, path.dirname(n.rel), src);
      if (fs.existsSync(srcAbs) && !fs.existsSync(destAbs)) {
        fs.mkdirSync(path.dirname(destAbs), { recursive: true });
        fs.copyFileSync(srcAbs, destAbs);
      }
    }
  }

  const found = groups.flatMap(g => g.docs.map(d => ({ g, d }))).find(({ d }) => d.allNodesInDoc.some(x => x.rel === n.rel));
  let page;
  if (found) {
    const { g, d } = found;
    const isEntry = d.entryNode.rel === n.rel;
    const isTab = d.tabs && d.tabs.some(t => t.node.rel === n.rel);
    const isKid = d.kids && d.kids.some(k => k.node && k.node.rel === n.rel);
    page = { group: g.id, doc: d.id };
    if (isTab) page.tab = d.tabs.find(t => t.node.rel === n.rel).id;
    if (isKid) page.screenLabel = d.kids.find(k => k.node && k.node.rel === n.rel).label;
    if (isEntry && d.tabs) page.tab = d.tabs[0] && d.tabs.find(t => t.node.rel === n.rel) ? page.tab : page.tab;
  } else {
    const rootDoc = rootDocs.find(rd => rd.node.rel === n.rel);
    page = { group: 'solution', doc: rootDoc ? rootDoc.id : 'root-' + slugify(n.rel), title: rootDoc ? rootDoc.label : title };
  }

  const html = pageShell({
    title,
    rel: n.htmlRel,
    page,
    body: bodyHtml + originalLinkFor(n.rel),
    hasToc: headingCount > 0
  });
  writeFile(n.htmlRel, html);
}

// ---------------------------------------------------------------------------
// 7. Home page (dashboard)
// ---------------------------------------------------------------------------
const tierCards = groups.map(g => `<a class="stat" data-t="${esc(g.type)}" href="${esc(g.docs[0] ? g.docs[0].href : '#')}"><div class="stat-n">${g.docs.length}</div><div class="stat-l">${esc(g.label)}</div></a>`).join('');
const homeBody = `
<h1>${esc(solutionName)}</h1>
<p class="subtitle">Generated documentation shell. Everything PowerDocu and the SharePoint enricher produced is included below — recognized component types get a organized view; anything else still appears, grouped by where it sits on disk.</p>
<div class="stats">${tierCards}</div>
<h2 id="root-files">Solution-level files</h2>
${rootDocs.length
  ? '<ul>' + rootDocs.map(rd => `<li><a href="${esc(rd.href)}">${esc(rd.label)}</a></li>`).join('') + '</ul>'
  : '<p class="empty">No solution-level files were found at the root of the output folder.</p>'}
`;
writeFile('index.html', pageShell({ title: solutionName, rel: 'index.html', page: { group: 'solution', doc: 'sol-overview' }, body: homeBody, hasToc: true }));

console.log('Wrote shell to ' + shellFolder + ' (' + (nodes.length + 1) + ' page(s)).');
