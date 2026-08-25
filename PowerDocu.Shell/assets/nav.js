/* ==========================================================================
   Solutions Docs v2 — SHARED nav/tab/crumb/toc renderer.
   One copy of this file, referenced (not duplicated) by every generated
   page. Each page sets `window.PAGE = {group, doc, tab}` inline before this
   script loads; everything else — sidebar tree, tab bar, breadcrumbs,
   on-this-page rail, search, theme — is built from that plus nav-data.js.
   This is what keeps individual pages small: real content plus three
   `<script>`/`<link>` references, not an inlined copy of the whole tree.
   ========================================================================== */
(function () {
  var DEPTH = parseInt(document.body.dataset.depth || '0', 10);
  var PREFIX = DEPTH > 0 ? '../'.repeat(DEPTH) : '';
  var PAGE = window.PAGE || {};
  var TREE = window.NAV_TREE.groups;

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }
  function href(h) { return PREFIX + h; }

  function findDoc(docId) {
    for (var g of TREE) for (var d of (g.docs || [])) if (d.id === docId) return { group: g, doc: d };
    return null;
  }
  function findKid(kidId) {
    for (var g of TREE) for (var d of (g.docs || [])) for (var k of (d.kids || [])) if (k.id === kidId) return { group: g, doc: d, kid: k };
    return null;
  }

  // ---- sidebar --------------------------------------------------------
  function renderSidebar() {
    var el = document.getElementById('sidebar');
    if (!el) return;
    var html = '';
    TREE.forEach(function (g) {
      var groupActive = PAGE.group === g.id;
      html += '<a class="nav-group' + (groupActive ? ' active' : '') + '" data-t="' + g.type + '" href="' + href(g.docs[0].href) + '">' +
        '<span class="nav-group-ico">' + (ICONS[g.type] || '') + '</span>' +
        '<span class="nav-group-label">' + esc(g.label) + '</span>' +
        (g.count != null ? '<span class="nav-count">' + g.count + '</span>' : '') +
        '</a>';
      (g.docs || []).forEach(function (d) {
        var active = PAGE.doc === d.id;
        // A doc with kids (e.g. an app with screens) stays "open" — kids shown, doc row
        // itself just not highlighted — whenever the current page is one of its kids too.
        var onKid = !!(d.kids && d.kids.some(function (k) { return k.id === PAGE.doc; }));
        var open = active || onKid;
        var badge = d.placeholder ? '<span class="nav-placeholder-badge">Illustrative</span>' : '';
        var meta = d.meta ? '<span class="nav-doc-meta">' + esc(d.meta) + '</span>' : '';
        html += '<a class="nav-doc' + (active ? ' active' : '') + '" href="' + href(d.href) + '" title="' + esc(d.label) + '">' +
          '<span style="flex:1;overflow:hidden;text-overflow:ellipsis">' + esc(d.label) + '</span>' + meta + badge + '</a>';
        if (open && d.kids) {
          html += '<div class="nav-kids"><div class="nav-kids-label">Items · own pages</div>';
          d.kids.forEach(function (k) {
            var kActive = PAGE.doc === k.id;
            html += '<a class="nav-kid' + (kActive ? ' active' : '') + '" href="' + href(k.href) + '">' + esc(k.label) + '</a>';
          });
          html += '</div>';
        }
      });
    });
    el.innerHTML = html;
  }

  // ---- tab bar ----------------------------------------------------------
  function renderTabs() {
    var el = document.getElementById('tabbar');
    if (!el) return;
    var found = findDoc(PAGE.doc);
    if (!found || !found.doc.tabs) { el.remove(); return; }
    var base = PREFIX + found.doc.href.replace(/[^/]+$/, '');
    var html = '<nav class="tabbar">';
    found.doc.tabs.forEach(function (t) {
      var active = t.id === PAGE.tab;
      html += '<a class="tab' + (active ? ' active' : '') + '" href="' + base + t.file + '">' + esc(t.label) +
        (t.n != null ? '<span class="tab-n">' + t.n + '</span>' : '') + '</a>';
    });
    html += '</nav>';
    el.outerHTML = html;
  }

  // ---- breadcrumbs --------------------------------------------------------
  function renderCrumbs() {
    var el = document.getElementById('crumbs');
    if (!el) return;
    var found = findDoc(PAGE.doc);
    var parts = [[window.SITE_TITLE || 'Documentation', href('index.html')]];
    if (found) {
      parts.push([found.doc.label, href(found.doc.href)]);
      if (PAGE.screenLabel) parts.push([PAGE.screenLabel, '#']);
      else if (PAGE.tab && found.doc.tabs) {
        var t = found.doc.tabs.find(function (x) { return x.id === PAGE.tab; });
        if (t && t.id !== 'overview') parts.push([t.label, '#']);
      }
    } else {
      var viaKid = findKid(PAGE.doc);
      if (viaKid) {
        parts.push([viaKid.doc.label, href(viaKid.doc.href)]);
        parts.push([viaKid.kid.label, '#']);
      } else if (PAGE.title) {
        parts.push([PAGE.title, '#']);
      }
    }
    el.innerHTML = parts.map(function (p, i) {
      var last = i === parts.length - 1;
      return (i ? '<span class="sep">/</span>' : '') + (last ? '<span class="cur">' + esc(p[0]) + '</span>' : '<a href="' + p[1] + '">' + esc(p[0]) + '</a>');
    }).join('');
  }

  // ---- on-this-page rail (scroll-spy) --------------------------------------
  function buildToc() {
    var body = document.getElementById('tocBody');
    var article = document.getElementById('article');
    if (!body || !article) return;
    var heads = [].slice.call(article.querySelectorAll('h2[id], h3[id]'));
    if (!heads.length) { body.innerHTML = '<div class="toc-empty" style="font-size:11.5px;color:var(--text-faint);padding-left:11px">No sections</div>'; return; }
    body.innerHTML = '<ul class="toc-list">' + heads.map(function (h) {
      return '<li><a class="' + (h.tagName === 'H3' ? 'lvl3' : '') + '" href="#' + h.id + '" data-id="' + h.id + '">' + esc(h.textContent) + '</a></li>';
    }).join('') + '</ul>';
    var links = {};
    [].slice.call(body.querySelectorAll('a')).forEach(function (a) { links[a.dataset.id] = a; });
    var obs = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          Object.values(links).forEach(function (a) { a.classList.remove('active'); });
          if (links[e.target.id]) links[e.target.id].classList.add('active');
        }
      });
    }, { root: document.getElementById('main'), rootMargin: '0px 0px -70% 0px', threshold: 0 });
    heads.forEach(function (h) { obs.observe(h); });
  }

  // ---- search --------------------------------------------------------------
  function wireSearch() {
    var input = document.getElementById('search');
    var box = document.getElementById('results');
    if (!input || !box) return;
    function run(q) {
      q = q.trim().toLowerCase();
      if (!q) { box.classList.remove('open'); return; }
      var hits = window.SEARCH_INDEX.filter(function (x) { return x.n.toLowerCase().indexOf(q) >= 0; }).slice(0, 12);
      box.innerHTML = hits.length ? hits.map(function (x) {
        return '<a class="res" href="' + href(x.h) + '"><span class="res-name">' + esc(x.n) + '</span><span class="res-path">' + esc(x.p) + '</span></a>';
      }).join('') : '<div class="results-empty">No matches</div>';
      box.classList.add('open');
    }
    input.addEventListener('input', function (e) { run(e.target.value); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { var a = box.querySelector('.res'); if (a) location.href = a.getAttribute('href'); }
      if (e.key === 'Escape') { input.value = ''; box.classList.remove('open'); input.blur(); }
    });
    document.addEventListener('click', function (e) { if (!e.target.closest('.search-wrap')) box.classList.remove('open'); });
    document.addEventListener('keydown', function (e) {
      if ((e.key === '/' || ((e.ctrlKey || e.metaKey) && e.key === 'k')) && document.activeElement !== input) { e.preventDefault(); input.focus(); }
    });
  }

  // ---- theme -----------------------------------------------------------
  function wireTheme() {
    var btn = document.getElementById('themeBtn');
    if (!btn) return;
    btn.addEventListener('click', function () {
      var cur = document.documentElement.getAttribute('data-theme');
      var next = cur === 'dark' ? 'light' : cur === 'light' ? 'dark' : (matchMedia('(prefers-color-scheme: dark)').matches ? 'light' : 'dark');
      document.documentElement.setAttribute('data-theme', next);
    });
  }

  // ---- action card deep-link (details[id] opened + scrolled via #hash) ----
  function openHashTarget() {
    var id = location.hash.slice(1);
    if (!id) return;
    var el = document.getElementById(id);
    if (!el) return;
    if (el.tagName === 'DETAILS') el.open = true;
    setTimeout(function () { el.scrollIntoView({ block: 'start', behavior: 'smooth' }); }, 30);
  }

  var ICONS = {
    flow: '<svg viewBox="0 0 20 20"><path d="M11.6 1.5 4 11h4.2l-.8 7.5L16 9h-4.2z"/></svg>',
    app: '<svg viewBox="0 0 20 20"><rect x="2.5" y="2.5" width="6.4" height="6.4" rx="1.4"/><rect x="11.1" y="2.5" width="6.4" height="6.4" rx="1.4"/><rect x="2.5" y="11.1" width="6.4" height="6.4" rx="1.4"/><rect x="11.1" y="11.1" width="6.4" height="6.4" rx="1.4"/></svg>',
    sp: '<svg viewBox="0 0 20 20"><path d="M8.6 2a5.6 5.6 0 0 1 5.5 4.6 4.3 4.3 0 0 1 3.7 4.2 4.3 4.3 0 0 1-4.3 4.3H8.4a4.9 4.9 0 0 1-.6-9.7A5.6 5.6 0 0 1 8.6 2Z"/></svg>',
    sol: '<svg viewBox="0 0 20 20"><path d="M10 1.5 2.5 5.6v8.8L10 18.5l7.5-4.1V5.6ZM10 3.6l5.2 2.8L10 9.3 4.8 6.4Zm-5.8 4.3 5 2.7v5.5l-5-2.7Zm7 8.2v-5.5l5-2.7v5.5Z"/></svg>',
    ref: '<svg viewBox="0 0 20 20"><path d="M4 2.5h9.5L17 6v11.5H4Zm8.6 1.6v2.6h2.6ZM6.4 9h7.2v1.4H6.4Zm0 3.2h7.2v1.4H6.4Z"/></svg>'
  };

  document.addEventListener('DOMContentLoaded', function () {
    renderSidebar();
    renderTabs();
    renderCrumbs();
    buildToc();
    wireSearch();
    wireTheme();
    openHashTarget();
  });
})();
