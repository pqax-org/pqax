// The storage stack per table: one horizontal bar per table, split into the live data, the non-live files, the
// deleted rows, the metadata with the footers, and the stale columns. In bytes, the bars share one scale and the
// largest table fills the width. In shares, every bar fills the width. The tables come by waste, largest first. The
// view is plain HTML, a grid per row, so that the bars stretch with the width and the label goes above the bar on a
// phone. A component that the file does not have is never drawn as a zero: a dashed mark after the bar shows it.
import { applyBaseOptions, attachKeyboard, attachTooltip, createRoot, html, makeItem, observeWidth, widthOf } from "./dom.js";
import { formatBytes, formatShare } from "./format.js";
import { sortTables } from "./model.js";
import type { BaseOptions, ViewHandle } from "./dom.js";
import type { StackComponent, StackComponentKey, StorageModel, TableModel, TableOrder } from "./model.js";

/** The scale of the bars: bytes, to the scale of the largest table, or shares, each bar filling the width. */
export type StackMode = "bytes" | "shares";

/** Options of {@link renderTableStack}. */
export interface StackOptions extends BaseOptions {
  /** Bytes: bars to the scale of the largest table. Shares: every bar fills the width, split by the shares of its table. Default "bytes". */
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

/** The segments of a bar, in the order of the bar. `metadata` sums the `metadata` and `footers` components. */
const SEGMENTS = ["live", "nonLive", "deleted", "metadata", "stale"] as const;

type SegmentKey = (typeof SEGMENTS)[number];

const NAMES: Readonly<Record<SegmentKey, string>> = {
  live: "Live data",
  nonLive: "Non-live files",
  deleted: "Deleted rows (estimate)",
  metadata: "Metadata and footers",
  stale: "Stale columns",
};

/** What the parts of the metadata segment are called when one of them is missing. */
const METADATA_PARTS: Readonly<Record<"metadata" | "footers", string>> = {
  metadata: "the metadata size",
  footers: "the footer size",
};

/** Below this width in pixels, the label goes above the bar. */
const NARROW = 480;

/** One segment of a bar. */
interface Segment {
  readonly key: SegmentKey;
  readonly bytes: number;
  readonly share: number | null;
  /** Whether a field that the segment needs is missing. */
  readonly absent: boolean;
  /** Whether nothing of the segment is known. */
  readonly unknown: boolean;
  /** For the metadata segment, the parts that are missing. */
  readonly missing: readonly string[];
  readonly source?: "files" | "table";
}

function componentOf(table: TableModel, key: StackComponentKey): StackComponent {
  return table.stack.components.find((c) => c.key === key) ?? { key, bytes: 0, share: null, absent: true };
}

/** The five segments of a table, from its six components. */
function segmentsOf(table: TableModel): Segment[] {
  const total = table.stack.total;
  return SEGMENTS.map((key): Segment => {
    if (key === "metadata") {
      const metadata = componentOf(table, "metadata");
      const footers = componentOf(table, "footers");
      const bytes = metadata.bytes + footers.bytes;
      const missing = [metadata.absent ? METADATA_PARTS.metadata : null, footers.absent ? METADATA_PARTS.footers : null]
        .filter((part): part is string => part !== null);
      return {
        key, bytes, share: total > 0 ? bytes / total : null, absent: missing.length > 0, unknown: missing.length === 2, missing,
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

/** The figures of a table for the tooltip, the first line being the title. */
function linesOf(table: TableModel, segments: readonly Segment[], model: StorageModel, locale: string | undefined): string[] {
  const lines = [table.label];
  const source = model.sources.find((s) => s.key === table.sourceKey);
  if (source !== undefined) {
    lines.push(`Source ${source.label}`);
  }
  lines.push(`Total ${formatBytes(table.stack.total, locale)}`);
  for (const segment of segments) {
    if (segment.unknown) {
      lines.push(absentText(segment));
      continue;
    }
    let line = `${NAMES[segment.key]} ${formatBytes(segment.bytes, locale)} (${formatShare(segment.share, locale)})`;
    if (segment.missing.length > 0) {
      line += `, ${segment.missing.join(" and ")} not in the file`;
    }
    if (segment.source === "files") {
      line += ", estimated from the files table";
    } else if (segment.source === "table") {
      line += ", from the table totals";
    }
    lines.push(line);
  }
  lines.push(`Waste ${formatBytes(table.stack.waste, locale)} (${formatShare(table.stack.wasteShare, locale)})`);
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

/** The legend: the five segments, and the mark of a component that the file does not have. */
function legend(): HTMLElement {
  return html("div", { class: "pqax-legend" },
    ...SEGMENTS.map((key) => html("span", { class: "pqax-legend-item", "data-component": key },
      html("span", { class: "pqax-swatch", "aria-hidden": "true" }), NAMES[key])),
    html("span", { class: "pqax-legend-item pqax-legend-absent" },
      html("span", { class: "pqax-absent-mark", "aria-hidden": "true" }), "Not in the file"));
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
    const width = mode === "shares" ? segment.share ?? 0 : scale > 0 ? segment.bytes / scale : 0;
    bar.append(html("span", { class: "pqax-segment", "data-component": segment.key, style: `width: ${percent(width)}` }));
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
 * non-live files, the deleted rows, the metadata and footers, and the stale columns. A toggle switches the bars between
 * bytes and shares.
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
    root.toggleAttribute("data-narrow", widthOf(root, container) < NARROW);

    const limit = opts.maxTables !== undefined && Number.isFinite(opts.maxTables) ? Math.max(0, Math.floor(opts.maxTables)) : Infinity;
    const tables = sortTables(current.tables, opts.order ?? "waste").slice(0, limit);
    const scale = tables.reduce((largest, t) => Math.max(largest, t.stack.total), 0);
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
