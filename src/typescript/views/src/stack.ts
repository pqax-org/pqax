// The storage stack per table: one horizontal bar per table, split into the live data and the waste (the non-live
// files, the unreferenced files, the deleted rows, the stale columns), with the overhead (the metadata and the footers)
// after a gap. The tables come by waste, largest first. In bytes, the bars share one scale and the largest table fills
// the width. In shares, the live data and the waste of every table fill the same width, and the overhead goes beyond
// it, since it is outside the denominator of the shares. The view is plain HTML, a grid per row, so that the bars
// stretch with the width and the label goes above the bar on a phone. A component that the file does not have is never
// drawn as a zero: a dashed mark after the bar shows it.
import { NARROW_WIDTH, applyBaseOptions, attachKeyboard, attachTooltip, createRoot, html, makeItem, observeWidth, widthOf } from "./dom.js";
import { formatBytes, formatCount, formatNumber, formatShare, formatTime } from "./format.js";
import { sortTables } from "./model.js";
import type { BaseOptions, ViewHandle } from "./dom.js";
import type { StackComponent, StackComponentKey, StorageModel, TableModel, TableOrder } from "./model.js";

/** The scale of the bars: bytes, to the scale of the largest table, or shares of the data of each table. */
export type StackMode = "bytes" | "shares";

/** Options of {@link renderTableStack}. */
export interface StackOptions extends BaseOptions {
  /** Bytes: bars to the scale of the largest table. Shares: the live data and the waste of every bar fill the same width, the overhead beyond it. Default "bytes". */
  readonly mode?: StackMode;
  /** The order of the tables. Default "waste". */
  readonly order?: TableOrder;
  /** Whether to draw the bytes/shares toggle. Default true. */
  readonly showToggle?: boolean;
  /** The most tables to draw, the first in the order. Default all. */
  readonly maxTables?: number;
  /** Called when the user changes the mode with the toggle. */
  readonly onModeChange?: (mode: StackMode) => void;
}

/** The segments of a bar, in the order of the bar. `overhead` sums the `metadata` and `footers` components. */
const SEGMENTS = ["live", "nonLive", "unreferenced", "deleted", "stale", "overhead"] as const;

type SegmentKey = (typeof SEGMENTS)[number];

const NAMES: Readonly<Record<SegmentKey, string>> = {
  live: "Live data",
  nonLive: "Non-live files",
  unreferenced: "Unreferenced files",
  deleted: "Deleted rows (estimate)",
  stale: "Stale columns",
  overhead: "Overhead: metadata and footers",
};

/** What the parts of the overhead segment are called when one of them is missing. */
const OVERHEAD_PARTS: Readonly<Record<"metadata" | "footers", string>> = {
  metadata: "the metadata size",
  footers: "the footer size",
};

/** One segment of a bar. */
interface Segment {
  readonly key: SegmentKey;
  readonly bytes: number;
  /** The share of the data of the table: the live data and the waste. */
  readonly share: number | null;
  /** Whether a field that the segment needs is missing. */
  readonly absent: boolean;
  /** Whether nothing of the segment is known. */
  readonly unknown: boolean;
  /** For the overhead segment, the parts that are missing. */
  readonly missing: readonly string[];
  readonly source?: "files" | "table";
}

function componentOf(table: TableModel, key: StackComponentKey): StackComponent {
  return table.stack.components.find((c) => c.key === key) ?? { key, bytes: 0, share: null, absent: true };
}

/** The six segments of a table, from its seven components. */
function segmentsOf(table: TableModel): Segment[] {
  const data = table.stack.data;
  return SEGMENTS.map((key): Segment => {
    if (key === "overhead") {
      const metadata = componentOf(table, "metadata");
      const footers = componentOf(table, "footers");
      const bytes = metadata.bytes + footers.bytes;
      const missing = [metadata.absent ? OVERHEAD_PARTS.metadata : null, footers.absent ? OVERHEAD_PARTS.footers : null]
        .filter((part): part is string => part !== null);
      return {
        key, bytes, share: data > 0 ? bytes / data : null, absent: missing.length > 0, unknown: missing.length === 2, missing,
      };
    }
    const component = componentOf(table, key);
    return {
      key, bytes: component.bytes, share: component.share, absent: component.absent, unknown: component.absent, missing: [],
      ...(component.source !== undefined ? { source: component.source } : {}),
    };
  });
}

/** What the file lacks for a segment, for the title of its mark and the tooltip. */
function absentText(segment: Segment): string {
  if (segment.unknown) {
    return `${NAMES[segment.key]} not in the file`;
  }
  return `${NAMES[segment.key]}: ${segment.missing.join(" and ")} not in the file`;
}

/** "between A and B", or "on A" when the two times read the same to the minute or one is missing. Null when both are missing. */
function range(from: string | null, to: string | null): string | null {
  if (from === null && to === null) {
    return null;
  }
  const start = formatTime(from ?? to);
  const end = formatTime(to ?? from);
  return start === end ? `on ${start}` : `between ${start} and ${end}`;
}

/** A count with its noun, singular for one. */
function counted(count: number | null, noun: string, locale: string | undefined): string {
  return `${formatCount(count, locale)} ${noun}${count === 1 ? "" : "s"}`;
}

/** The lines on the files that the table no longer uses: what the log says, what the listing says, and when. */
function storageLines(table: TableModel, locale: string | undefined): string[] {
  const lines: string[] = [];
  if (table.nonLiveFileSize !== null || table.nonLiveFileCount !== null) {
    const tracked = `The log tracks ${counted(table.nonLiveFileCount, "file", locale)} that left the table, ${formatBytes(table.nonLiveFileSize, locale)}`;
    lines.push(table.stack.listed ? tracked : `${tracked}; a VACUUM may have deleted some of them`);
  }
  if (table.unreferencedFileCount !== null || table.unreferencedFileSize !== null) {
    lines.push(`The storage holds ${counted(table.unreferencedFileCount, "file", locale)} that the table does not reference, ${formatBytes(table.unreferencedFileSize, locale)}`);
  }
  const removed = range(table.nonLiveOldestRemovedAt, table.nonLiveNewestRemovedAt);
  if (removed !== null) {
    lines.push(`Non-live files removed ${removed}`);
  }
  return lines;
}

/** The lines on the history: the rate of the commits, the last one, the maintenance operations, and the partitions. */
function historyLines(table: TableModel, locale: string | undefined): string[] {
  const m = table.maintenance;
  const lines: string[] = [];
  const last = m.daysSinceLastCommit !== null ? `last commit ${counted(m.daysSinceLastCommit, "day", locale)} ago` : null;
  if (m.commitsPerDay !== null) {
    const read = m.commitsRead ?? m.commitCount;
    lines.push(`${formatNumber(m.commitsPerDay, locale)} commits a day over the ${counted(read, "commit", locale)} read${last !== null ? `, ${last}` : ""}`);
  } else if (last !== null) {
    lines.push(`Last commit ${counted(m.daysSinceLastCommit, "day", locale)} ago`);
  }
  if (m.hasOptimize !== null) {
    lines.push(m.hasOptimize ? `OPTIMIZE last ${range(m.lastOptimizeAt, null) ?? "at an unknown time"}` : "No OPTIMIZE in the commits read");
  }
  if (m.hasVacuum !== null) {
    lines.push(m.hasVacuum ? `VACUUM last ${range(m.lastVacuumAt, null) ?? "at an unknown time"}` : "No VACUUM in the commits read");
  }
  const p = table.partitions;
  if (p.count !== null && p.count > 0) {
    const changed = range(p.oldestModifiedAt, p.newestModifiedAt);
    lines.push(`${counted(p.count, "partition", locale)}${changed !== null ? `, changed ${changed}` : ""}`);
  }
  return lines;
}

/** The figures of a table for the tooltip, the first line being the title. */
function linesOf(table: TableModel, segments: readonly Segment[], model: StorageModel, locale: string | undefined): string[] {
  const lines = [table.label];
  const source = model.sources.find((s) => s.key === table.sourceKey);
  if (source !== undefined) {
    lines.push(`Source ${source.label}`);
  }
  lines.push(`Total ${formatBytes(table.stack.total, locale)}: data ${formatBytes(table.stack.data, locale)}, overhead ${formatBytes(table.stack.overhead, locale)}`);
  for (const segment of segments) {
    if (segment.unknown) {
      lines.push(absentText(segment));
      continue;
    }
    if (segment.key === "overhead") {
      const metadata = componentOf(table, "metadata");
      const footers = componentOf(table, "footers");
      const logFiles = table.metadataFileCount !== null ? ` in ${counted(table.metadataFileCount, "file", locale)} of the log` : "";
      const parts = [
        metadata.absent ? "metadata not in the file" : `metadata ${formatBytes(metadata.bytes, locale)}${logFiles}`,
        footers.absent ? "footers not in the file" : `footers ${formatBytes(footers.bytes, locale)}`,
      ];
      lines.push(`Overhead ${formatBytes(segment.bytes, locale)} (${formatShare(segment.share, locale)} beyond the data): ${parts.join(", ")}`);
      continue;
    }
    let line = `${NAMES[segment.key]} ${formatBytes(segment.bytes, locale)} (${formatShare(segment.share, locale)})`;
    if (segment.key === "nonLive") {
      line += table.stack.listed ? ", at most, still in the storage" : ", as the log gives them";
    } else if (segment.key === "unreferenced") {
      line += ", beyond the non-live files";
    }
    if (segment.source === "files") {
      line += ", estimated from the files table";
    } else if (segment.source === "table") {
      line += ", from the table totals";
    }
    lines.push(line);
  }
  lines.push(`Waste ${formatBytes(table.stack.waste, locale)} (${formatShare(table.stack.wasteShare, locale)} of the data)`);
  lines.push(...storageLines(table, locale), ...historyLines(table, locale));
  return lines;
}

function modeOf(options: StackOptions): StackMode {
  return options.mode === "shares" ? "shares" : "bytes";
}

function percent(value: number): string {
  return `${Math.max(0, Math.min(100, value * 100)).toFixed(3)}%`;
}

/** The toolbar with the toggle of the mode. */
function toolbar(mode: StackMode): HTMLElement {
  const button = (value: StackMode, text: string) =>
    html("button", { type: "button", "data-mode": value, "aria-pressed": String(mode === value) }, text);
  return html("div", { class: "pqax-toolbar" },
    html("div", { class: "pqax-toggle", role: "group", "aria-label": "Scale" }, button("bytes", "Bytes"), button("shares", "Shares")));
}

/** The legend: the six segments, and the mark of a component that the file does not have. */
function legend(): HTMLElement {
  return html("div", { class: "pqax-legend" },
    ...SEGMENTS.map((key) => html("span", { class: "pqax-legend-item", "data-component": key },
      html("span", { class: "pqax-swatch", "aria-hidden": "true" }), NAMES[key])),
    html("span", { class: "pqax-legend-item pqax-legend-absent" },
      html("span", { class: "pqax-absent-mark", "aria-hidden": "true" }), "Not in the file"));
}

/**
 * The scale of the bars. In bytes, the largest total. In shares, the width that the data of every table takes, so that
 * the table whose overhead is the largest share of its data still fits.
 */
function scaleOf(tables: readonly TableModel[], mode: StackMode): number {
  if (mode === "shares") {
    const widest = tables.reduce((most, t) => (t.stack.data > 0 ? Math.max(most, t.stack.total / t.stack.data) : most), 1);
    return 1 / widest;
  }
  return tables.reduce((largest, t) => Math.max(largest, t.stack.total), 0);
}

/** One row: the label, the bar, the marks of the missing components, and the value. */
function row(table: TableModel, model: StorageModel, mode: StackMode, scale: number, showSource: boolean, locale: string | undefined): HTMLElement {
  const segments = segmentsOf(table);
  const source = model.sources.find((s) => s.key === table.sourceKey);
  const label = html("div", { class: "pqax-label" },
    html("span", { class: "pqax-name" }, table.label),
    showSource && source !== undefined ? html("span", { class: "pqax-source" }, source.label) : null);
  const bar = html("div", { class: "pqax-bar", "aria-hidden": "true" });
  for (const segment of segments) {
    if (segment.bytes <= 0) {
      continue;
    }
    const width = mode === "shares" ? (segment.share ?? 0) * scale : scale > 0 ? segment.bytes / scale : 0;
    const overhead = segment.key === "overhead" ? " pqax-overhead" : "";
    bar.append(html("span", { class: `pqax-segment${overhead}`, "data-component": segment.key, style: `width: ${percent(width)}` }));
  }
  const marks = html("span", { class: "pqax-marks" },
    ...segments.filter((s) => s.absent).map((s) =>
      html("span", { class: "pqax-absent pqax-absent-mark", "data-component": s.key, title: absentText(s) })));
  const value = mode === "shares"
    ? `${formatShare(table.stack.wasteShare, locale)} waste`
    : formatBytes(table.stack.total, locale);
  const element = html("div", { class: "pqax-row" }, label, bar, marks, html("span", { class: "pqax-value" }, value));
  makeItem(element, table.key, linesOf(table, segments, model, locale));
  return element;
}

/**
 * Marks the segments narrower than 3 pixels, but the largest of each bar, with `data-thin`, so that the CSS draws them
 * without the gap and the least width: a sliver of 1 pixel plus a gap of 2 would make a tiny segment look large. The
 * largest segment keeps its least width, so that a tiny table still shows.
 */
function markThin(rows: HTMLElement): void {
  const width = rows.querySelector(".pqax-bar")?.clientWidth ?? 0;
  if (width <= 0) {
    return;
  }
  for (const bar of rows.querySelectorAll<HTMLElement>(".pqax-bar")) {
    const segments = [...bar.querySelectorAll<HTMLElement>(".pqax-segment")];
    const widths = segments.map((s) => (parseFloat(s.style.width) / 100) * width);
    const largest = widths.indexOf(Math.max(...widths));
    segments.forEach((segment, i) => segment.toggleAttribute("data-thin", i !== largest && widths[i]! < 3));
  }
}

/** A selector that finds again the element with the focus after a new render, null when the focus is elsewhere. */
function focusSelector(root: HTMLElement): string | null {
  const active = document.activeElement;
  if (!(active instanceof Element) || !root.contains(active)) {
    return null;
  }
  const key = active.getAttribute("data-key");
  if (key !== null) {
    return `.pqax-item[data-key="${CSS.escape(key)}"]`;
  }
  const mode = active.closest(".pqax-toggle") !== null ? active.getAttribute("data-mode") : null;
  return mode !== null ? `.pqax-toggle button[data-mode="${CSS.escape(mode)}"]` : null;
}

/**
 * Draws the storage stack of each table: one row per table, sorted by waste, with a bar split into the live data, the
 * non-live files, the unreferenced files, the deleted rows, and the stale columns, then the overhead of the metadata and
 * the footers. A toggle switches the bars between bytes and shares.
 * @param container The element that receives the view.
 * @param model The model from `buildModel`.
 * @param options The mode, the order, and the common options.
 */
export function renderTableStack(container: HTMLElement, model: StorageModel, options: StackOptions = {}): ViewHandle<StackOptions> {
  let current = model;
  let opts: StackOptions = { ...options };
  const root = createRoot(container, "stack", opts);
  const detachKeyboard = attachKeyboard(root);
  let detachTooltip: (() => void) | null = null;
  let disposed = false;

  const render = (focus: string | null = focusSelector(root)) => {
    const mode = modeOf(opts);
    const locale = opts.locale;
    root.setAttribute("data-mode", mode);
    if (!root.hasAttribute("aria-label")) {
      root.setAttribute("aria-label", "Storage stack per table");
    }
    for (const child of [...root.children]) {
      if (!child.classList.contains("pqax-tooltip")) {
        child.remove();
      }
    }
    if (current.tables.length === 0) {
      detachTooltip?.();
      detachTooltip = null;
      root.append(html("p", { class: "pqax-empty" }, "No tables in this footprint."));
      return;
    }
    detachTooltip ??= attachTooltip(root);
    root.toggleAttribute("data-narrow", widthOf(root, container) < NARROW_WIDTH);

    const limit = opts.maxTables !== undefined && Number.isFinite(opts.maxTables) ? Math.max(0, Math.floor(opts.maxTables)) : Infinity;
    const tables = sortTables(current.tables, opts.order ?? "waste").slice(0, limit);
    const scale = scaleOf(tables, mode);
    const showSource = current.sources.length > 1;
    const rows = html("div", { class: "pqax-rows" }, ...tables.map((t) => row(t, current, mode, scale, showSource, locale)));
    const marks = tables.reduce((most, t) => Math.max(most, segmentsOf(t).filter((s) => s.absent).length), 0);
    rows.setAttribute("data-marks", String(marks));
    rows.style.setProperty("--pqax-stack-marks", `${marks}rem`);

    const head = html("div", { class: "pqax-stack-head" }, opts.showToggle === false ? null : toolbar(mode), legend());
    root.prepend(head, rows);
    markThin(rows);
    if (focus !== null) {
      root.querySelector<HTMLElement>(focus)?.focus({ preventScroll: true });
    }
  };

  const onClick = (event: MouseEvent) => {
    const target = event.target instanceof Element ? event.target : null;
    const button = target?.closest(".pqax-toggle button") ?? null;
    if (button !== null && root.contains(button)) {
      const mode = button.getAttribute("data-mode");
      if ((mode === "bytes" || mode === "shares") && mode !== modeOf(opts)) {
        opts = { ...opts, mode };
        render(`.pqax-toggle button[data-mode="${mode}"]`);
        opts.onModeChange?.(mode);
      }
      return;
    }
    const item = target?.closest(".pqax-item") ?? null;
    const key = item !== null && root.contains(item) ? item.getAttribute("data-key") : null;
    if (key !== null) {
      opts.onSelect?.(key);
    }
  };
  root.addEventListener("click", onClick);
  const stopObserving = observeWidth(root, () => render());
  render(null);

  return {
    element: root,
    update(nextModel?: StorageModel, nextOptions?: Partial<StackOptions>) {
      if (disposed) {
        return;
      }
      current = nextModel ?? current;
      opts = { ...opts, ...nextOptions };
      applyBaseOptions(root, opts);
      render();
    },
    dispose() {
      if (disposed) {
        return;
      }
      disposed = true;
      stopObserving();
      root.removeEventListener("click", onClick);
      detachKeyboard();
      detachTooltip?.();
      detachTooltip = null;
      root.remove();
    },
  };
}
