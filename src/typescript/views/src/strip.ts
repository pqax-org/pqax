// The distribution of the file sizes per table: one row per table on a log scale that every row shares, with the
// range from the smallest to the largest file, a box from the first to the third quartile, the median, and one dot per
// file when the files table gives the sizes. A column on the right shows the share of the files that are V-Ordered. A
// dashed line marks each of the two small file thresholds of the model, 8 MiB and 128 MiB by default. The view is one
// SVG, drawn again when the width changes; on a phone the label goes above the plot.
import { NARROW_WIDTH, applyBaseOptions, attachKeyboard, attachTooltip, createRoot, html, makeItem, observeWidth, svg, widthOf } from "./dom.js";
import { formatBytes, formatCount, formatShare } from "./format.js";
import type { BaseOptions, ViewHandle } from "./dom.js";
import type { SmallFilesModel, StorageModel, TableModel } from "./model.js";

/** Options of {@link renderFileStrip}. */
export interface StripOptions extends BaseOptions {
  /** The order of the tables: by median file size, largest first ("size"), by file count ("count"), or by label. Default "size". */
  readonly order?: "size" | "count" | "label";
  /** Whether to draw one dot per file when the files table gives the sizes. Default true. */
  readonly showFiles?: boolean;
  /** The most dots for one table; beyond it, an even sample of the sorted sizes. Default 400. */
  readonly maxDots?: number;
  /** The height of a row in pixels. Default 28. */
  readonly rowHeight?: number;
  /** Whether to draw the two small file thresholds of the model as dashed lines. Default true. */
  readonly showThreshold?: boolean;
}

/** The least width of the label column, about 7rem. */
const LABEL_MIN = 112;
/** The width of the V-Order column, about 5.5rem. */
const VORDER = 88;
/** The width of the V-Order track inside its column. */
const TRACK = 40;
/** The space between the columns. */
const GAP = 12;
/** The height of the line of the label above the plot, on a phone. */
const LABEL_LINE = 16;
/** The least space between two labels of the axis. */
const TICK_SPACE = 52;
/** The radius of a dot. */
const DOT = 2.5;
/** The width of a character of 12px, when the text cannot be measured. */
const CHAR = 6.5;

/** The order of the tables of the strip. Ties go by the order of the file. */
function orderTables(tables: readonly TableModel[], order: StripOptions["order"]): TableModel[] {
  const byLabel = (a: TableModel, b: TableModel) => (a.label < b.label ? -1 : a.label > b.label ? 1 : 0);
  const descending = (a: number | null, b: number | null) => (a === null ? (b === null ? 0 : 1) : b === null ? -1 : b - a);
  return [...tables].sort((a, b) => {
    if (order === "label") {
      return byLabel(a, b) || a.row - b.row;
    }
    if (order === "count") {
      return descending(a.files.count, b.files.count) || a.row - b.row;
    }
    return descending(a.files.median, b.files.median) || byLabel(a, b) || a.row - b.row;
  });
}

/** The known sizes of a table: the five figures and the smallest and largest file. */
function knownSizes(table: TableModel): number[] {
  const f = table.files;
  const values = [f.minimum, f.firstQuartile, f.median, f.thirdQuartile, f.maximum];
  if (f.sizes !== null && f.sizes.length > 0) {
    values.push(f.sizes[0]!, f.sizes[f.sizes.length - 1]!);
  }
  return values.filter((v): v is number => v !== null && Number.isFinite(v));
}

/** The log domain: half the smallest size to twice the largest, at least 1 byte, never empty. */
function domainOf(tables: readonly TableModel[]): [number, number] {
  let lo = Infinity;
  let hi = -Infinity;
  for (const table of tables) {
    for (const value of knownSizes(table)) {
      lo = Math.min(lo, value);
      hi = Math.max(hi, value);
    }
  }
  if (!Number.isFinite(lo)) {
    return [1024, 1024 ** 3];
  }
  const low = Math.max(1, lo / 2);
  return [low, Math.max(hi * 2, low * 4)];
}

/** The ticks of the axis: powers of 1024, then x10 and x100 of each, then x2 and x5, until there are three. */
function ticksOf(lo: number, hi: number, x: (value: number) => number): number[] {
  const tiers = [[1], [1, 10, 100], [1, 2, 5, 10, 20, 50, 100, 200, 500]];
  let ticks: number[] = [];
  for (const tier of tiers) {
    ticks = [];
    for (let unit = 1; unit <= 1024 ** 5; unit *= 1024) {
      for (const factor of tier) {
        const value = unit * factor;
        if (value >= lo && value <= hi) {
          ticks.push(value);
        }
      }
    }
    if (ticks.length >= 3) {
      break;
    }
  }
  ticks.sort((a, b) => a - b);
  const kept: number[] = [];
  for (const tick of ticks) {
    if (kept.length === 0 || x(tick) - x(kept[kept.length - 1]!) >= TICK_SPACE) {
      kept.push(tick);
    }
  }
  return kept;
}

/** Bytes for the axis: as `formatBytes`, without a fraction of zeros, so `1 MB` and not `1.00 MB`. */
function tickText(value: number, locale: string | undefined): string {
  const separator = (1.5).toLocaleString(locale).replace(/\d/g, "").charAt(0) || ".";
  const escaped = separator.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return formatBytes(value, locale).replace(new RegExp(`${escaped}0+(?= )`), "");
}

/** The width of a text in the SVG, measured when the text is in the document, else estimated. */
function textWidth(text: SVGTextElement): number {
  try {
    const width = text.getComputedTextLength();
    if (width > 0) {
      return width;
    }
  } catch {
    // Not rendered: estimate.
  }
  return (text.textContent ?? "").length * CHAR;
}

/** Shortens a text in place with an ellipsis until it fits a width. */
function fitText(text: SVGTextElement, full: string, width: number): void {
  text.textContent = full;
  let measured = textWidth(text);
  if (measured <= width || full.length <= 1) {
    return;
  }
  let length = Math.max(1, Math.floor((full.length * width) / measured) - 1);
  for (;;) {
    text.textContent = `${full.slice(0, length).trimEnd()}…`;
    measured = textWidth(text);
    if (measured <= width || length <= 1) {
      return;
    }
    length = Math.max(1, Math.min(length - 1, Math.floor((length * width) / measured)));
  }
}

/** An even sample of sorted sizes, the smallest and the largest always in. */
function sample(sizes: readonly number[], most: number): readonly number[] {
  if (sizes.length <= most) {
    return sizes;
  }
  if (most <= 1) {
    return most < 1 ? [] : [sizes[Math.floor(sizes.length / 2)]!];
  }
  const result: number[] = [];
  for (let i = 0; i < most; i++) {
    result.push(sizes[Math.round((i * (sizes.length - 1)) / (most - 1))]!);
  }
  return result;
}

/** The figures of a table for the tooltip, the first line being the title. */
function linesOf(table: TableModel, locale: string | undefined): string[] {
  const f = table.files;
  const lines = [table.label];
  const count = f.count === null ? "n/a files" : `${formatCount(f.count, locale)} ${f.count === 1 ? "file" : "files"}`;
  lines.push(`${count}, ${formatBytes(f.sum, locale)}`);
  if (f.source === null) {
    lines.push("No file sizes in the file");
  } else {
    const b = (value: number | null) => formatBytes(value, locale);
    lines.push(`Min ${b(f.minimum)} · Q1 ${b(f.firstQuartile)} · Median ${b(f.median)} · Q3 ${b(f.thirdQuartile)} · Max ${b(f.maximum)}`);
    lines.push(f.source === "files" ? "From the files table" : "From the fileSizes distribution");
  }
  const v = f.vOrder;
  if (v === null) {
    lines.push("V-Order unknown");
  } else {
    const bytes = v.bytesShare !== null ? `, ${formatShare(v.bytesShare, locale)} of the bytes` : "";
    lines.push(`${formatCount(v.files, locale)} of ${formatCount(v.knownFiles, locale)} files V-Ordered (${formatShare(v.share, locale)})${bytes}`);
  }
  for (const small of [f.tinyFiles, f.smallFiles]) {
    const line = smallLine(small, locale);
    if (line !== null) {
      lines.push(line);
    }
  }
  return lines;
}

/** The files below a threshold: the exact count with its share, or the lower bound of the share. */
function smallLine(small: SmallFilesModel, locale: string | undefined): string | null {
  const threshold = formatBytes(small.threshold, locale);
  if (small.count !== null) {
    return `${formatCount(small.count, locale)} ${small.count === 1 ? "file" : "files"} below ${threshold} (${formatShare(small.shareAtLeast, locale)})`;
  }
  return small.shareAtLeast !== null ? `At least ${formatShare(small.shareAtLeast, locale)} of the files below ${threshold}` : null;
}

/** A selector that finds again the item with the focus after a new render, null when the focus is elsewhere. */
function focusSelector(root: HTMLElement): string | null {
  const active = document.activeElement;
  const key = active instanceof Element && root.contains(active) ? active.getAttribute("data-key") : null;
  return key !== null ? `.pqax-item[data-key="${CSS.escape(key)}"]` : null;
}

/**
 * Draws the distribution of the file sizes of each table on a log scale: the range, the quartiles, the median, one dot
 * per file when the files table gives the sizes, and the V-Order share in a column on the right.
 * @param container The element that receives the view.
 * @param model The model from `buildModel`.
 * @param options The order, the dots, the threshold, and the common options.
 */
export function renderFileStrip(container: HTMLElement, model: StorageModel, options: StripOptions = {}): ViewHandle<StripOptions> {
  let current = model;
  let opts: StripOptions = { ...options };
  const root = createRoot(container, "strip", opts);
  const detachKeyboard = attachKeyboard(root);
  let detachTooltip: (() => void) | null = null;
  let disposed = false;

  const render = (focus: string | null = focusSelector(root)) => {
    const locale = opts.locale;
    if (!root.hasAttribute("aria-label")) {
      root.setAttribute("aria-label", "File sizes per table");
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

    // The geometry: three columns, or on a phone the label above the plot and the V-Order column.
    const width = Math.max(240, widthOf(root, container));
    const narrow = width < NARROW_WIDTH;
    root.toggleAttribute("data-narrow", narrow);
    const rowHeight = opts.rowHeight !== undefined && Number.isFinite(opts.rowHeight) && opts.rowHeight >= 12 ? opts.rowHeight : 28;
    const labelWidth = narrow ? 0 : Math.max(LABEL_MIN, Math.round(width * 0.3));
    const plotLeft = narrow ? DOT + 2 : labelWidth + GAP;
    const vorderLeft = width - VORDER;
    const plotRight = vorderLeft - GAP;
    const stride = rowHeight + (narrow ? LABEL_LINE : 0);
    const tables = orderTables(current.tables, opts.order ?? "size");
    const [lo, hi] = domainOf(tables);
    const span = Math.log(hi) - Math.log(lo);
    const x = (value: number) => plotLeft + ((Math.log(Math.min(hi, Math.max(lo, value))) - Math.log(lo)) / span) * (plotRight - plotLeft);
    const first = current.tables[0]!.files;
    const thresholds = opts.showThreshold === false ? [] : [first.tinyFiles.threshold, first.smallFiles.threshold]
      .filter((t) => Number.isFinite(t) && t >= lo && t <= hi);
    const axisY = thresholds.length > 0 ? 34 : 20;
    const top = axisY + 4;
    const height = top + tables.length * stride + 4;

    const chart = svg("svg", { class: "pqax-strip-chart", width, height, viewBox: `0 0 ${width} ${height}` });
    root.prepend(chart);

    // The axis, the gridlines, and the header of the V-Order column.
    const axis = svg("g", { class: "pqax-axis", "aria-hidden": "true" });
    chart.append(axis);
    for (const tick of ticksOf(lo, hi, x)) {
      const tx = x(tick);
      const text = svg("text", { class: "pqax-tick", x: tx, y: 12, "text-anchor": "middle" }, tickText(tick, locale));
      axis.append(svg("line", { class: "pqax-grid", x1: tx, x2: tx, y1: axisY, y2: height - 4 }), text);
      // A label at an end of the plot leans inward, so that it stays off the label and V-Order columns.
      const half = textWidth(text) / 2;
      if (tx + half > plotRight + 4) {
        text.setAttribute("text-anchor", "end");
      } else if (tx - half < (narrow ? 0 : plotLeft - 4)) {
        text.setAttribute("text-anchor", "start");
      }
    }
    axis.append(
      svg("line", { class: "pqax-axis-line", x1: plotLeft, x2: plotRight, y1: axisY, y2: axisY }),
      svg("text", { class: "pqax-vorder-head", x: vorderLeft, y: 12 }, "V-Order"));
    for (const threshold of thresholds) {
      const tx = x(threshold);
      axis.append(svg("line", { class: "pqax-threshold", x1: tx, x2: tx, y1: 18, y2: height - 4 }));
      const text = thresholds.length > 1 ? `< ${tickText(threshold, locale)}` : `small files below ${formatBytes(threshold, locale)}`;
      const label = svg("text", { class: "pqax-threshold-label", x: tx + 4, y: 29 }, text);
      axis.append(label);
      if (tx + 4 + textWidth(label) > plotRight && tx - 4 - textWidth(label) >= (narrow ? 0 : plotLeft - GAP)) {
        label.setAttribute("x", String(tx - 4));
        label.setAttribute("text-anchor", "end");
      }
    }

    // The rows.
    const showFiles = opts.showFiles !== false;
    const maxDots = opts.maxDots !== undefined && Number.isFinite(opts.maxDots) ? Math.max(0, Math.floor(opts.maxDots)) : 400;
    const box = Math.max(6, Math.min(16, rowHeight * 0.45));
    tables.forEach((table, index) => {
      const f = table.files;
      const rowTop = top + index * stride;
      const cy = rowTop + (narrow ? LABEL_LINE : 0) + rowHeight / 2;
      const g = svg("g", { class: "pqax-strip-row" });
      makeItem(g, table.key, linesOf(table, locale));
      chart.append(g);
      g.append(svg("rect", { class: "pqax-hit", x: 1, y: rowTop + 1, width: width - 2, height: stride - 2, rx: 4 }));
      const label = svg("text", { class: "pqax-label", x: narrow ? 2 : 0, y: narrow ? rowTop + 12 : cy + 4 });
      g.append(label);
      fitText(label, table.label, narrow ? width - 4 : labelWidth - 4);

      if (f.source === null) {
        g.append(svg("text", { class: "pqax-absent", x: plotLeft, y: cy + 4 }, "No file sizes in the file"));
      } else {
        if (f.minimum !== null && f.maximum !== null) {
          g.append(svg("line", { class: "pqax-range", x1: x(f.minimum), x2: x(f.maximum), y1: cy, y2: cy }));
        }
        if (f.firstQuartile !== null && f.thirdQuartile !== null) {
          const x1 = x(f.firstQuartile);
          g.append(svg("rect", { class: "pqax-box", x: x1, y: cy - box / 2, width: Math.max(1, x(f.thirdQuartile) - x1), height: box, rx: 2 }));
        }
        if (showFiles && f.sizes !== null) {
          const dots = sample(f.sizes, maxDots);
          dots.forEach((value, i) => {
            // A fixed jitter, so that the dots of equal sizes spread and the drawing does not change between renders.
            const jitter = dots.length > 1 ? (((i * 0.6180339887) % 1) - 0.5) * (box - DOT) : 0;
            g.append(svg("circle", { class: "pqax-dot", cx: x(value), cy: cy + jitter, r: DOT }));
          });
        }
        if (f.median !== null) {
          const mx = x(f.median);
          g.append(svg("line", { class: "pqax-median", x1: mx, x2: mx, y1: cy - box / 2 - 2, y2: cy + box / 2 + 2 }));
        }
      }

      // The V-Order column.
      if (f.vOrder === null) {
        g.append(svg("text", { class: "pqax-absent", "data-component": "vOrder", x: vorderLeft, y: cy + 4 }, "n/a"));
      } else {
        const share = Math.max(0, Math.min(1, f.vOrder.share));
        g.append(
          svg("rect", { class: "pqax-vorder-track", x: vorderLeft, y: cy - 3, width: TRACK, height: 6, rx: 3 }),
          svg("rect", { class: "pqax-vorder-fill", x: vorderLeft, y: cy - 3, width: share * TRACK, height: 6, rx: 3 }),
          svg("text", { class: "pqax-vorder-value", x: width, y: cy + 4, "text-anchor": "end" }, formatShare(f.vOrder.share, locale)));
      }
    });

    if (focus !== null) {
      root.querySelector<SVGElement>(focus)?.focus({ preventScroll: true });
    }
  };

  const onClick = (event: MouseEvent) => {
    const target = event.target instanceof Element ? event.target : null;
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
    update(nextModel?: StorageModel, nextOptions?: Partial<StripOptions>) {
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
