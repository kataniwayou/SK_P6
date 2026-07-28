#!/usr/bin/env node
/**
 * phase-88-panel-read.js — batch Grafana panel DOM reader (Phase 88, plan 88-01).
 *
 * WHAT THIS IS
 *   The verdict surface for Phase 88. Locked constraint 2 says the RENDERED panel is the verdict:
 *   Prometheus may be queried for diagnosis, never for the decision. This script turns rendered
 *   Grafana panels into numbers by reading their DOM text, and prints one machine-parseable line
 *   per (panel, window) pair.
 *
 * HOW IT IS RUN
 *   Never directly. It is handed to the playwright-skill executor, which copies it into its own
 *   directory and require()s it:
 *       node <skillDir>/run.js <abs path to scripts/phase-88-panel-read.js>
 *   Consequences that shape this file:
 *     - run.js prints banner lines to stdout BEFORE this script runs, so the caller cannot
 *       JSON-parse the whole capture. Hence the sentinels below.
 *     - run.js does process.chdir(__dirname), so cwd is the SKILL directory, not the repo.
 *       Every path this script writes MUST therefore be absolute (see SCREENSHOT_DIR).
 *     - run.js leaves a script that already has require( plus an async IIFE unmodified, which is
 *       why this file is a complete standalone script rather than a fragment.
 *
 * ENV CONTRACT (process environment, passed straight through by run.js)
 *   GRAFANA_URL        default 'http://127.0.0.1:3000'
 *   DASHBOARD_UID      default 'skp-business'
 *   PANEL_IDS          REQUIRED, comma-separated, e.g. '9' or '1,2,3,4,5,9'
 *   WINDOWS            REQUIRED, semicolon-separated absolute epoch-ms pairs 'from:to;from:to;...'
 *                      Every pair MUST be the same width; the batch is refused otherwise.
 *   VIEWPORT_W         default '1920'
 *   VIEWPORT_H         default '1080'
 *   LOCATOR_MODE       'viewpanel' (default) | 'headerwalk'
 *   PANEL_TITLES       REQUIRED only when LOCATOR_MODE=headerwalk; comma-separated, positionally
 *                      aligned with PANEL_IDS
 *   SCREENSHOT_DIR     optional, MUST be absolute; one PNG per (panel, window) named
 *                      panel-<id>-<fromMs>.png
 *   GRAFANA_BASIC_AUTH optional base64 'user:pass'; applied as an Authorization header when non-empty
 *   HEADLESS           optional, 'false' opens a visible browser (debugging only; default headless)
 *   PANEL_READ_SELFTEST optional, '1' runs the HERMETIC parser self-test and exits, launching no
 *                      browser and touching no cluster (see runSelfTest below)
 *
 * OUTPUT (stdout)
 *   ##PANEL-JSON##      one compact-JSON line per (panel, window) pair, window-major order.
 *   ##PANEL-BATCH-END## exactly one, LAST: {"requested":N,"emitted":M}. This lets the caller tell a
 *                       TRUNCATED batch from a complete one instead of silently banding a partial
 *                       sample set as if it were whole.
 *   ##PANEL-SELFTEST##  exactly one, only under PANEL_READ_SELFTEST=1.
 *
 * WHY THE WINDOW MUST BE ABSOLUTE
 *   A timeseries legend's Mean/Max are computed over the VISIBLE time range, and the dashboard ships
 *   a sliding relative default plus a 30 s auto-refresh. A baseline and an after-capture taken over
 *   sliding, overlapping windows are not comparable — the baseline can even contain the fault. So
 *   every read pins absolute epoch-millisecond from/to of identical width, and appends `&refresh=`
 *   (empty) so the render cannot move underneath the assertion.
 *
 * WHY THE VIEWPORT IS PINNED
 *   Grafana derives the query step from max(datasource.timeInterval, range / maxDataPoints), and
 *   maxDataPoints defaults to the panel's PIXEL WIDTH. A different browser size gives a different
 *   step, hence potentially a different $__rate_interval, hence differently smeared numbers. The
 *   viewport is set before the first navigation and recorded in every reading.
 *
 * WHY ONE BROWSER SESSION FOR THE WHOLE BATCH
 *   A 14-panel x 10-window baseline is 140 reads. A browser launch per read would dominate the run
 *   and would also drift the wall-clock across a capture that is supposed to be one measurement.
 *   One context, one page, every pair.
 *
 * RULES THIS FILE OBEYS
 *   - Selectors are Grafana's own data-testid values only (verified against the v12.3.9 source tag).
 *     Emotion class names are build hashes and are never selected on.
 *   - Screenshots are EVIDENCE, never a verdict. No pixel diffing anywhere.
 *   - 'No data' is a first-class panel STATE (panelState: "NoData"), not a read failure: panel 9 is
 *     deliberately unguarded so a dead keeper renders "No data" rather than a comfortable 0.
 *   - A read that throws does not abort the batch; that pair is emitted with panelState "Error" plus
 *     an error field, and the shortfall shows up in the BATCH-END counts. Nothing ever exits silently.
 */

const fs = require('fs');
const path = require('path');

// playwright is resolved LAZILY and DEFENSIVELY. Two reasons, both load-bearing:
//   1. run.js copies this file into the skill directory before require()ing it, so `playwright`
//      resolves from the SKILL's node_modules — but the hermetic parser self-test below must be
//      runnable straight from the repo, where that module does not resolve at all.
//   2. A bare top-level require() that throws kills the process before ANY sentinel line is printed,
//      which the caller can only report as "the reader produced no sentinel line at all". Capturing
//      it here turns a missing dependency into a NAMED batch-end error instead.
let chromium = null;
let playwrightLoadError = null;
try { chromium = require('playwright').chromium; }
catch (e) { playwrightLoadError = (e && e.message) ? e.message : String(e); }

const READER_VERSION = '1';
const SENTINEL_READING = '##PANEL-JSON##';
const SENTINEL_BATCH_END = '##PANEL-BATCH-END##';

// Grafana 12.3.9 e2e-selector literals. The attribute VALUE genuinely begins with the words
// "data-testid " — it reads oddly and it is correct.
const SEL_PANEL_CONTENT = '[data-testid="data-testid panel content"]';
const SEL_PANEL_ERROR = '[data-testid="data-testid Panel status error"]';
const SEL_PANEL_STATUS_ANY = '[data-testid^="data-testid Panel status"]';
const SEL_LOADING_BAR = '[data-testid="Panel loading bar"]';
const SEL_LEGEND_SERIES_PREFIX = '[data-testid^="data-testid VizLegend series"]';
const TESTID_LEGEND_SERIES = 'data-testid VizLegend series ';

const NAV_TIMEOUT_MS = 45000;
const READ_TIMEOUT_MS = 20000;

function emitReading(obj) {
  console.log(SENTINEL_READING + ' ' + JSON.stringify(obj));
}

function emitBatchEnd(requested, emitted, error) {
  const payload = { requested: requested, emitted: emitted, readerVersion: READER_VERSION };
  if (error) { payload.error = String(error); }
  console.log(SENTINEL_BATCH_END + ' ' + JSON.stringify(payload));
}

function envOr(name, fallback) {
  const v = process.env[name];
  return (v === undefined || v === null || String(v).trim() === '') ? fallback : String(v).trim();
}

function splitList(text, separator) {
  return String(text).split(separator).map(function (s) { return s.trim(); })
    .filter(function (s) { return s.length > 0; });
}

// 'from:to' -> { fromMs, toMs, widthMs }. Returns null when the pair is not two positive integers.
function parseWindow(spec) {
  const parts = String(spec).split(':');
  if (parts.length !== 2) { return null; }
  const fromMs = Number(parts[0]);
  const toMs = Number(parts[1]);
  if (!Number.isFinite(fromMs) || !Number.isFinite(toMs)) { return null; }
  if (!Number.isInteger(fromMs) || !Number.isInteger(toMs)) { return null; }
  if (fromMs <= 0 || toMs <= fromMs) { return null; }
  return { fromMs: fromMs, toMs: toMs, widthMs: toMs - fromMs };
}

// First numeric token of a stat panel's big number: optional leading '-', digits, optional decimal
// point, optional trailing '%'. Returns null when there is no numeric token at all (which is how a
// unit-only or textual stat render stays visible rather than being invented as 0).
function parseFirstNumber(text) {
  if (text === null || text === undefined) { return null; }
  const m = String(text).match(/-?\d+(?:\.\d+)?/);
  if (!m) { return null; }
  const n = Number(m[0]);
  return Number.isFinite(n) ? n : null;
}

// A legend cell that is not numeric yields null rather than dropping the row: a legend-name-only
// change (the `or vector(0)` guard ceasing to fire) must stay observable even with no number.
function parseCell(text) {
  if (text === null || text === undefined) { return null; }
  const t = String(text).trim();
  if (t.length === 0) { return null; }
  if (!/^-?\d/.test(t) && !/^-?\.\d/.test(t)) { return null; }
  const n = parseFirstNumber(t);
  return n;
}

/**
 * Parse `data-testid panel content` innerText.
 *   stat        -> a single big number, possibly with a unit suffix       -> statValue set, series []
 *   timeseries  -> table-legend rows of `name`, `Mean`, `Max`             -> series set, statValue null
 *
 * AMENDMENT 1 (88-PROBE-DECISIONS.md Record 3, measured 2026-07-28 — REQUIRED before any Phase-88
 * baseline is captured). On THIS Grafana render `innerText` puts each legend series NAME on its own
 * line and the numeric cells on the line that FOLLOWS it:
 *
 *     Name<TAB>Mean<TAB>Max
 *     <blank>
 *     keeper-99b8c574b-29mzq
 *     <TAB>0.200 ops/s<TAB>0.200 ops/s
 *
 * The original parser required >= 2 cells on ONE line, so every name line was discarded as noise and
 * every emitted row read {"name":"","nameTrimmed":"","mean":<number>}: values bound, names bound to
 * NOTHING. Panel 2's entire discrimination signal is a legend-name change, so a baseline captured on
 * the unamended parser would have been structurally blind to what it exists to measure. A lone
 * non-numeric line is therefore HELD as a pending name and paired with the next values line whose own
 * first cell is blank. POSITIONAL row index (seriesIndex) is the reliable axis; name matching is not.
 *
 * Names are carried VERBATIM, with a separate nameTrimmed for convenience — but note that `innerText`
 * NORMALISES THE TRAILING SPACE AWAY ENTIRELY (Record 3 supplementary measurement): panel 2's
 * label-less guard series renders as the bare word `consumed`, NOT as `consumed ` with a trailing
 * space. The observable signal is the SUFFIX change `consumed` -> `consumed keeper`; the
 * trailing-space formulation the research proposed does not exist on this render and must not be
 * looked for.
 */
function parsePanelText(rawText) {
  const result = { statValue: null, series: [] };
  if (rawText === null || rawText === undefined) { return result; }

  // Deliberately NOT trimming the lines before splitting on tabs: the cell text is the payload.
  const lines = String(rawText).split(/\r?\n/).filter(function (l) { return l.trim().length > 0; });

  let meanIndex = 1;
  let maxIndex = 2;
  let headerSeen = false;
  const rows = [];
  let pendingName = null;   // AMENDMENT 1: the name line awaiting its values line

  for (let i = 0; i < lines.length; i++) {
    let cells = lines[i].split('\t');
    if (cells.length < 2) {
      // innerText does not always emit tabs for a grid-rendered legend; fall back to a run of
      // two-or-more spaces, which single numbers and unit suffixes never contain.
      const spaced = lines[i].split(/ {2,}/);
      if (spaced.length >= 2) { cells = spaced; }
    }
    if (cells.length < 2) {
      // Not a values line. If it carries text at all it is a legend NAME line — hold it VERBATIM
      // rather than discarding it (that discard was the Record-3 defect).
      if (lines[i].trim().length > 0) { pendingName = lines[i]; }
      continue;
    }

    const tail = cells.slice(1).map(function (c) { return c.trim().toLowerCase(); });
    const isHeader = !headerSeen && tail.indexOf('mean') >= 0;
    if (isHeader) {
      headerSeen = true;
      const mi = tail.indexOf('mean');
      const xi = tail.indexOf('max');
      if (mi >= 0) { meanIndex = mi + 1; }
      if (xi >= 0) { maxIndex = xi + 1; }
      pendingName = null;   // a column header is never a series name
      continue;
    }

    let nameSource = 'inline';
    if (String(cells[0]).trim().length === 0 && pendingName !== null) {
      cells = cells.slice();
      cells[0] = pendingName;
      nameSource = 'precedingLine';
    }
    // Consumed (or deliberately dropped when the values line carried its own name): a pending name
    // must never bind to a SECOND row, which would silently mislabel every row after the first.
    pendingName = null;
    rows.push({ cells: cells, nameSource: nameSource });
  }

  if (rows.length === 0) {
    // No legend rows -> a stat panel (or an empty/NoData render).
    result.statValue = parseFirstNumber(rawText);
    return result;
  }

  for (let i = 0; i < rows.length; i++) {
    const cells = rows[i].cells;
    const name = cells[0];
    result.series.push({
      // seriesIndex is the POSITIONAL axis Record 3 amendment 2 requires: per-series reads are taken
      // by row index, because a name-matched read returns nothing whenever names fail to bind.
      seriesIndex: i,
      name: name,
      nameTrimmed: String(name).trim(),
      nameSource: nameSource_of(rows[i]),
      mean: (cells.length > meanIndex) ? parseCell(cells[meanIndex]) : null,
      max: (cells.length > maxIndex) ? parseCell(cells[maxIndex]) : null
    });
  }
  return result;
}

// Tiny accessor so the row shape stays a single object rather than two parallel arrays.
function nameSource_of(row) { return row && row.nameSource ? row.nameSource : 'inline'; }

// Recover series names verbatim from the legend testid attribute values.
//
// AMENDMENT 3 (88-PROBE-DECISIONS.md Record 3): this route is MEASURED ABSENT on this Grafana render.
// `[data-testid^="data-testid VizLegend series"]` matched ZERO elements on every reading of the
// wave-0 probe (`legendSeriesNames` came back empty throughout, `Panel9LegendNames: []`). The code is
// kept because it is harmless and version-dependent — a future Grafana may emit the attribute again —
// but NOTHING may depend on it. It is a bonus corroboration channel, never a fallback, and the caller
// is told whether it produced anything via the additive `legendTestIdAvailable` field.
async function readLegendSeriesNames(scope) {
  try {
    const names = await scope.$$eval(SEL_LEGEND_SERIES_PREFIX, function (els, prefix) {
      return els.map(function (el) {
        const v = el.getAttribute('data-testid') || '';
        return v.indexOf(prefix) === 0 ? v.substring(prefix.length) : v;
      });
    }, TESTID_LEGEND_SERIES);
    return Array.isArray(names) ? names : [];
  } catch (e) {
    return [];
  }
}

function buildUrl(grafanaUrl, dashboardUid, panelId, fromMs, toMs, includeViewPanel) {
  // `panel-<id>` is the Scenes-era view-panel format (Grafana 11.3+); a bare numeric viewPanel is
  // documented as redirecting to the dashboard with "Panel not found".
  // `&refresh=` (empty) disables the dashboard's shipped auto-refresh so the render cannot move
  // under the assertion. from/to are ABSOLUTE epoch ms — never a relative range.
  const view = includeViewPanel ? ('?viewPanel=panel-' + panelId + '&from=') : ('?from=');
  return grafanaUrl + '/d/' + dashboardUid + '/' + view + fromMs + '&to=' + toMs +
    '&var-source=All&var-pod=All&kiosk&refresh=';
}

/**
 * HERMETIC SELF-TEST (PANEL_READ_SELFTEST=1) — launches NO browser, touches no cluster.
 *
 * It asserts AMENDMENT 1 against the VERBATIM innerText that the wave-0 probe actually recorded from
 * this Grafana instance (88-PROBE-DECISIONS.md Record 3), so "the parser now binds legend names" is a
 * re-runnable check rather than a claim in a summary. Run it with:
 *     PANEL_READ_SELFTEST=1 node scripts/phase-88-panel-read.js
 * Prints one ##PANEL-SELFTEST## line and exits 0 (all cases pass) or 1 (any case failed).
 */
function runSelfTest() {
  const cases = [];
  function check(label, actual, expected) {
    const a = JSON.stringify(actual);
    const e = JSON.stringify(expected);
    cases.push({ label: label, ok: a === e, actual: actual, expected: expected });
  }

  // 1 + 2. The two rawText payloads recorded live by the wave-0 probe. The name is on its own line
  //        and the numbers on the line after it — the exact shape that used to bind no names at all.
  const panel9 = 'Name\tMean\tMax\n\nkeeper-99b8c574b-29mzq\n\t0.200 ops/s\t0.200 ops/s\n\nkeeper-99b8c574b-x4wqr\n\t0.200 ops/s\t0.200 ops/s';
  const p9 = parsePanelText(panel9);
  check('panel9-names', p9.series.map(function (s) { return s.nameTrimmed; }),
    ['keeper-99b8c574b-29mzq', 'keeper-99b8c574b-x4wqr']);
  check('panel9-means', p9.series.map(function (s) { return s.mean; }), [0.2, 0.2]);
  check('panel9-indices', p9.series.map(function (s) { return s.seriesIndex; }), [0, 1]);

  const panel2 = 'Name\tMean\tMax\n\nconsumed\n\t0 ops/s\t0 ops/s\n\nsent\n\t0 ops/s\t0 ops/s';
  const p2 = parsePanelText(panel2);
  check('panel2-names', p2.series.map(function (s) { return s.nameTrimmed; }), ['consumed', 'sent']);
  check('panel2-means', p2.series.map(function (s) { return s.mean; }), [0, 0]);
  // The SUFFIX transition is what remains observable once the guard stops firing (the trailing-space
  // formulation does not survive innerText and must never be looked for).
  const p2after = parsePanelText('Name\tMean\tMax\n\nconsumed keeper\n\t0.4 ops/s\t0.5 ops/s\n\nsent keeper\n\t0.4 ops/s\t0.5 ops/s');
  check('panel2-suffix-transition', p2after.series.map(function (s) { return s.nameTrimmed; }),
    ['consumed keeper', 'sent keeper']);

  // 3. A legend that DOES put the name inline must keep working — the amendment is additive.
  const inline = parsePanelText('Name\tMean\tMax\nfoo\t1.5\t2\nbar\t3\t4');
  check('inline-names', inline.series.map(function (s) { return s.nameTrimmed; }), ['foo', 'bar']);
  check('inline-source', inline.series.map(function (s) { return s.nameSource; }), ['inline', 'inline']);

  // 4. A stat panel is still a stat panel: no rows, one number, no invented series.
  const stat = parsePanelText('0');
  check('stat-value', stat.statValue, 0);
  check('stat-no-series', stat.series.length, 0);
  const statLabelled = parsePanelText('gap\n1.23 K');
  check('stat-labelled-value', statLabelled.statValue, 1.23);

  // 5. A pending name must NEVER bind to a second row. If it did, one stray line would mislabel every
  //    row after it — a silent, plausible-looking wrong answer, which is worse than an empty name.
  const stray = parsePanelText('Name\tMean\tMax\nstray-line\n\t1\t1\n\t2\t2');
  check('stray-name-binds-once', stray.series.map(function (s) { return s.nameTrimmed; }), ['stray-line', '']);
  check('stray-name-sources', stray.series.map(function (s) { return s.nameSource; }), ['precedingLine', 'inline']);

  const failed = cases.filter(function (c) { return !c.ok; });
  console.log('##PANEL-SELFTEST## ' + JSON.stringify({
    readerVersion: READER_VERSION,
    total: cases.length,
    failed: failed.length,
    cases: cases
  }));
  process.exitCode = (failed.length === 0) ? 0 : 1;
}

if (String(process.env.PANEL_READ_SELFTEST || '') === '1') {
  runSelfTest();
} else {
(async () => {
  // ---------------------------------------------------------------------------------------------
  // env contract
  // ---------------------------------------------------------------------------------------------
  const grafanaUrl = envOr('GRAFANA_URL', 'http://127.0.0.1:3000').replace(/\/+$/, '');
  const dashboardUid = envOr('DASHBOARD_UID', 'skp-business');
  const panelIdsRaw = envOr('PANEL_IDS', '');
  const windowsRaw = envOr('WINDOWS', '');
  const viewportWidth = parseInt(envOr('VIEWPORT_W', '1920'), 10);
  const viewportHeight = parseInt(envOr('VIEWPORT_H', '1080'), 10);
  const locatorMode = envOr('LOCATOR_MODE', 'viewpanel').toLowerCase();
  const panelTitlesRaw = envOr('PANEL_TITLES', '');
  const screenshotDirRaw = envOr('SCREENSHOT_DIR', '');
  const basicAuth = envOr('GRAFANA_BASIC_AUTH', '');
  const headless = envOr('HEADLESS', 'true').toLowerCase() !== 'false';

  const panelIds = splitList(panelIdsRaw, ',');
  const panelTitles = splitList(panelTitlesRaw, ',');
  const windowSpecs = splitList(windowsRaw, ';');

  if (panelIds.length === 0) {
    emitBatchEnd(0, 0, 'PANEL_IDS is required (comma-separated panel ids)');
    process.exitCode = 1;
    return;
  }
  if (windowSpecs.length === 0) {
    emitBatchEnd(0, 0, 'WINDOWS is required (semicolon-separated absolute epoch-ms fromMs:toMs pairs)');
    process.exitCode = 1;
    return;
  }

  const windows = [];
  for (let i = 0; i < windowSpecs.length; i++) {
    const w = parseWindow(windowSpecs[i]);
    if (!w) {
      emitBatchEnd(0, 0, 'malformed WINDOWS entry "' + windowSpecs[i] + '" (expected fromMs:toMs epoch milliseconds)');
      process.exitCode = 1;
      return;
    }
    windows.push(w);
  }

  // Unequal windows are NOT comparable: a timeseries legend Mean is computed over the visible range,
  // so a wider window averages over more data. Refuse the whole batch rather than emit numbers that
  // look comparable and are not.
  const width0 = windows[0].widthMs;
  for (let i = 1; i < windows.length; i++) {
    if (windows[i].widthMs !== width0) {
      emitBatchEnd(0, 0, 'WINDOWS are not all the same width (' + width0 + ' ms vs ' +
        windows[i].widthMs + ' ms at index ' + i + '); the legend Mean is computed over the visible range, so unequal windows are not comparable');
      process.exitCode = 1;
      return;
    }
  }

  const useHeaderWalk = (locatorMode === 'headerwalk');
  if (useHeaderWalk && panelTitles.length !== panelIds.length) {
    emitBatchEnd(0, 0, 'LOCATOR_MODE=headerwalk requires PANEL_TITLES positionally aligned with PANEL_IDS (' +
      panelTitles.length + ' titles vs ' + panelIds.length + ' ids)');
    process.exitCode = 1;
    return;
  }

  let screenshotDir = null;
  let screenshotWarning = null;
  if (screenshotDirRaw.length > 0) {
    if (!path.isAbsolute(screenshotDirRaw)) {
      // cwd is the SKILL directory (run.js chdir's there), so a relative path would write outside
      // the repository and into a tool's install directory. Refuse and report instead.
      screenshotWarning = 'SCREENSHOT_DIR "' + screenshotDirRaw + '" is not absolute; no screenshot was written (cwd is the playwright skill directory, not the repo)';
    } else {
      screenshotDir = screenshotDirRaw;
      try {
        fs.mkdirSync(screenshotDir, { recursive: true });
      } catch (e) {
        screenshotDir = null;
        screenshotWarning = 'could not create SCREENSHOT_DIR: ' + e.message;
      }
    }
  }

  const requested = panelIds.length * windows.length;
  let emitted = 0;

  let browser = null;
  let context = null;
  let page = null;

  if (chromium === null) {
    emitBatchEnd(requested, 0, 'the playwright module could not be loaded: ' + playwrightLoadError);
    process.exitCode = 1;
    return;
  }

  try {
    browser = await chromium.launch({ headless: headless });
    const contextOptions = { viewport: { width: viewportWidth, height: viewportHeight } };
    if (basicAuth.length > 0) {
      contextOptions.extraHTTPHeaders = { Authorization: 'Basic ' + basicAuth };
    }
    context = await browser.newContext(contextOptions);
    page = await context.newPage();
    // Pinned BEFORE the first navigation: maxDataPoints derives from the panel's pixel width, which
    // sets the query step, which sets $__rate_interval.
    await page.setViewportSize({ width: viewportWidth, height: viewportHeight });
  } catch (e) {
    emitBatchEnd(requested, 0, 'could not open a browser: ' + (e && e.message ? e.message : String(e)));
    process.exitCode = 1;
    if (browser) { try { await browser.close(); } catch (e2) { /* best effort */ } }
    return;
  }

  try {
    // Window-major order: for each window, for each panel. Deterministic, and it keeps every panel's
    // reads for one window adjacent in the output.
    for (let wi = 0; wi < windows.length; wi++) {
      const w = windows[wi];

      if (useHeaderWalk) {
        // One navigation per window; every panel is read off the same full-dashboard render.
        const dashUrl = buildUrl(grafanaUrl, dashboardUid, null, w.fromMs, w.toMs, false);
        try {
          await page.goto(dashUrl, { waitUntil: 'networkidle', timeout: NAV_TIMEOUT_MS });
        } catch (e) {
          // Fall through: each panel read below will record its own Error line.
        }
      }

      for (let pi = 0; pi < panelIds.length; pi++) {
        const panelId = panelIds[pi];
        const url = buildUrl(grafanaUrl, dashboardUid, panelId, w.fromMs, w.toMs, !useHeaderWalk);

        const reading = {
          panelId: panelId,
          dashboardUid: dashboardUid,
          fromMs: w.fromMs,
          toMs: w.toMs,
          windowWidthSeconds: Math.round(w.widthMs / 1000),
          viewportWidth: viewportWidth,
          viewportHeight: viewportHeight,
          locatorMode: useHeaderWalk ? 'headerwalk' : 'viewpanel',
          url: url,
          panelState: 'Error',
          rawText: null,
          series: [],
          legendSeriesNames: [],
          legendTestIdAvailable: false,
          statValue: null,
          screenshotPath: null,
          readerVersion: READER_VERSION,
          capturedUtc: new Date().toISOString()
        };
        if (screenshotWarning) { reading.warning = screenshotWarning; }

        try {
          let scope = page;
          let contentLocator = null;

          if (useHeaderWalk) {
            // Fallback locator for the case where viewPanel misbehaves on the live instance. The
            // header -> content DOM relationship is NOT itself a Grafana contract, which is exactly
            // why this mode is the fallback and not the default.
            const title = panelTitles[pi];
            const header = page.getByTestId('data-testid Panel header ' + title);
            await header.first().waitFor({ state: 'attached', timeout: READ_TIMEOUT_MS });
            contentLocator = header.first().locator(
              'xpath=following::*[@data-testid="data-testid panel content"][1]');
          } else {
            await page.goto(url, { waitUntil: 'networkidle', timeout: NAV_TIMEOUT_MS });
            // In viewPanel mode exactly one panel is on the page, so no panel scoping is needed.
            contentLocator = page.getByTestId('data-testid panel content').first();
          }

          // Wait for the query to FINISH rather than sleeping. The loading bar is absent entirely on
          // a fast render, so a detached-state wait that resolves immediately is the correct shape.
          await page.waitForSelector(SEL_LOADING_BAR, { state: 'detached', timeout: READ_TIMEOUT_MS })
            .catch(function () { /* absent on a fast render */ });

          // Bounded wait for non-empty panel text. On timeout we still read: an errored panel can
          // legitimately render no content, and that must surface as a state, not as a hang.
          await page.waitForFunction(function (sel) {
            const el = document.querySelector(sel);
            return !!el && typeof el.innerText === 'string' && el.innerText.trim().length > 0;
          }, SEL_PANEL_CONTENT, { timeout: READ_TIMEOUT_MS })
            .catch(function () { /* recorded below as NoData/Error via the actual text */ });

          const errorCount = await page.locator(SEL_PANEL_ERROR).count()
            .catch(function () { return 0; });
          let statusCount = 0;
          if (errorCount === 0) {
            statusCount = await page.locator(SEL_PANEL_STATUS_ANY).count()
              .catch(function () { return 0; });
          }

          let rawText = '';
          try {
            rawText = await contentLocator.innerText({ timeout: READ_TIMEOUT_MS });
          } catch (e) {
            rawText = '';
          }
          reading.rawText = rawText;

          if (errorCount > 0 || statusCount > 0) {
            reading.panelState = 'Error';
          } else if (/^no data$/i.test(String(rawText).trim())) {
            // A first-class STATE, not a read failure. Panel 9 is deliberately unguarded so a dead
            // keeper renders "No data" rather than a comfortable 0 — there, NoData IS the signal.
            reading.panelState = 'NoData';
          } else if (String(rawText).trim().length === 0) {
            reading.panelState = 'Error';
            reading.error = 'panel content rendered empty within ' + READ_TIMEOUT_MS + ' ms';
          } else {
            reading.panelState = 'Rendered';
          }

          if (reading.panelState === 'Rendered') {
            const parsed = parsePanelText(rawText);
            reading.series = parsed.series;
            reading.statValue = parsed.statValue;
            reading.legendSeriesNames = await readLegendSeriesNames(scope);
            // AMENDMENT 3: recorded so a reader can see whether the attribute route produced anything
            // at all. It was measured EMPTY on every wave-0 reading, so the loop below is a no-op on
            // this stack — the names now come from AMENDMENT 1's name-line pairing, not from here.
            reading.legendTestIdAvailable = (reading.legendSeriesNames.length > 0);
            for (let si = 0; si < reading.series.length; si++) {
              const trimmed = reading.series[si].nameTrimmed;
              for (let ni = 0; ni < reading.legendSeriesNames.length; ni++) {
                if (String(reading.legendSeriesNames[ni]).trim() === trimmed) {
                  reading.series[si].name = reading.legendSeriesNames[ni];
                  reading.series[si].nameSource = 'testid';
                  break;
                }
              }
            }
          }

          if (screenshotDir) {
            const shotPath = path.join(screenshotDir, 'panel-' + panelId + '-' + w.fromMs + '.png');
            try {
              await contentLocator.screenshot({ path: shotPath, timeout: READ_TIMEOUT_MS });
              reading.screenshotPath = shotPath;
            } catch (e) {
              try {
                await page.screenshot({ path: shotPath });
                reading.screenshotPath = shotPath;
              } catch (e2) {
                reading.screenshotPath = null;
                reading.warning = 'screenshot failed: ' + (e2 && e2.message ? e2.message : String(e2));
              }
            }
          }
        } catch (e) {
          // A single bad read never aborts the batch — the pair is emitted as Error and the run
          // continues, so one unreadable panel cannot destroy 139 good samples.
          reading.panelState = 'Error';
          reading.error = (e && e.message) ? e.message : String(e);
        }

        emitReading(reading);
        emitted++;
      }
    }
  } finally {
    try { if (page) { await page.close(); } } catch (e) { /* best effort */ }
    try { if (context) { await context.close(); } } catch (e) { /* best effort */ }
    try { if (browser) { await browser.close(); } } catch (e) { /* best effort */ }
    // ALWAYS last, ALWAYS exactly one: the caller detects a truncated batch from these counts.
    emitBatchEnd(requested, emitted, null);
  }
})();
}
