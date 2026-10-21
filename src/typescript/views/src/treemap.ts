// The column treemap: the compressed bytes of the footprint, from the sources down to the columns. The view shows the
// children of one node of `columnTree`, the focus, as tiles with areas proportional to their bytes, and the children
// of each tile nested inside it when there is room. The fill of a tile is the bin of its share of nulls; a node
// without totals keeps a small area and a hatch, so that it stays visible without reading as a value. A click on a tile
// with children moves the focus down; the breadcrumb, Backspace, and Escape move it up. No color is set here: the
// fills come from `data-bin` and the custom properties of styles.css, the rest from css/treemap.css.
import {
  applyBaseOptions, attachKeyboard, attachTooltip, createRoot, html, makeItem, observeWidth, svg, widthOf,
} from "./dom.js";
import type { BaseOptions, ViewHandle } from "./dom.js";
import { formatBytes, formatCount, formatNumber, formatShare } from "./format.js";
import { columnTree, findPath } from "./model.js";
import type { StorageModel, TreeNode } from "./model.js";
import { squarify } from "./squarify.js";
import type { LayoutRect } from "./squarify.js";

/** The options of {@link renderColumnTreemap}. */
export interface TreemapOptions extends BaseOptions {
  /** The height of the plot in pixels. Default 420. */
  readonly height?: number;
  /** The key of the node whose children are shown. Default "root". An unknown key falls back to the root. */
  readonly focus?: string;
  /** The share of its parent that each absent child (no totals) takes, so that it stays visible. Default 0.01. */
  readonly absentShare?: number;
  /** Called with the key of the new focus when the user drills down or up. */
  readonly onFocusChange?: (key: string) => void;
}

/** The bin of a null share: "none" for null, 0 for exactly 0, 1 up to 5%, 2 up to 20%, 3 up to 50%, 4 up to 80%, 5 above. */
export function nullShareBin(share: number | null): 0 | 1 | 2 | 3 | 4 | 5 | "none" {
  if (share === null || !Number.isFinite(share)) {
    return "none";
  }
  if (share <= 0) {
    return 0;
  }
  if (share <= 0.05) {
    return 1;
  }
  if (share <= 0.2) {
    return 2;
  }
  if (share <= 0.5) {
    return 3;
  }
  return share <= 0.8 ? 4 : 5;
}

/** The bins of the legend, in order, with their text. */
const LEGEND: readonly (readonly [ReturnType<typeof nullShareBin>, string])[] = [
  [0, "0%"], [1, "up to 5%"], [2, "up to 20%"], [3, "up to 50%"], [4, "up to 80%"], [5, "over 80%"], ["none", "No totals"],
];

const DEFAULT_HEIGHT = 420;
const DEFAULT_ABSENT_SHARE = 0.01;
/** Half the gap between two tiles: each rect is inset by it. */
const INSET = 1;
/** The left padding of a label. */
const PAD = 6;
/** The height of the header strip of a tile with nested children. */
const HEADER = 20;
const LABEL_SIZE = 12;
const SMALL_SIZE = 11;
/** The smallest tile that shows its children nested inside. */
const NEST_WIDTH = 72;
const NEST_HEIGHT = 60;

/** A counter that makes the id of the hatch pattern unique per view. */
let instances = 0;

/** The children of a node with their weights: the size, a share of the parent when absent, 1 each when the parent is empty. */
function weightsOf(parent: TreeNode, absentShare: number): { node: TreeNode; weight: number; index: number }[] {
  return parent.children.map((node, index) => ({
    node, index,
    weight: parent.size > 0 ? (node.absent ? parent.size * absentShare : node.size) : 1,
  }));
}

/** The path to the focus: the node of the key, else the root. A leaf gives way to its parent, which has children to show. */
function focusPath(tree: TreeNode, key: string): TreeNode[] {
  const path = findPath(tree, key) ?? [tree];
  while (path.length > 1 && path[path.length - 1]!.children.length === 0) {
    path.pop();
  }
  return path;
}

let context: CanvasRenderingContext2D | null | undefined;

/** A function that measures text in the font of an element, with a canvas; an estimate when there is no canvas. */
function measurer(reference: Element): (text: string, size: number) => number {
  if (context === undefined) {
    try {
      context = document.createElement("canvas").getContext("2d");
    } catch {
      context = null;
    }
  }
  const style = getComputedStyle(reference);
  const family = style.fontFamily || "sans-serif";
  let font = "";
  return (text, size) => {
    if (context === null || context === undefined) {
      return text.length * size * 0.6;
    }
    const next = `${size}px ${family}`;
    if (font !== next) {
      context.font = next;
      font = next;
    }
    return context.measureText(text).width;
  };
}

/** The shortest start of a label that is worth writing before an ellipsis. */
const MIN_CHARS = 3;
/** The ellipsis that ends a cut label. */
const ELLIPSIS = String.fromCharCode(0x2026);

/** The text cut with an ellipsis to fit a width, or null when too little of it fits. With `whole`, the text fits whole or not at all. */
function fit(text: string, width: number, size: number, measure: (text: string, size: number) => number, whole = false): string | null {
  if (width <= 0) {
    return null;
  }
  if (measure(text, size) <= width) {
    return text;
  }
  if (whole) {
    return null;
  }
  let low = 0;
  let high = text.length - 1;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    if (measure(`${text.slice(0, middle).trimEnd()}${ELLIPSIS}`, size) <= width) {
      low = middle;
    } else {
      high = middle - 1;
    }
  }
  return low < MIN_CHARS ? null : `${text.slice(0, low).trimEnd()}${ELLIPSIS}`;
}

/** The rect of a tile inside its cell, inset by half the gap. */
function inset(rect: LayoutRect, by: number): LayoutRect {
  return {
    x: rect.x + by, y: rect.y + by,
    width: Math.max(0, rect.width - 2 * by), height: Math.max(0, rect.height - 2 * by),
  };
}

/** The figures of a tile, for the tooltip and the accessible name. The first line is the title. */
function linesOf(node: TreeNode, focus: TreeNode, sourceLabels: ReadonlyMap<string, string>, locale: string | undefined): string[] {
  const lines = [node.label];
  if (node.kind === "table" && node.table !== undefined) {
    const source = sourceLabels.get(node.table.sourceKey);
    if (source !== undefined) {
      lines.push(`Source ${source}`);
    }
  }
  lines.push(`Compressed ${formatBytes(node.absent ? null : node.size, locale)}`);
  if (!node.absent && focus.size > 0) {
    lines.push(`${formatShare(node.size / focus.size, locale)} of ${focus.label}`);
  }
  lines.push(node.nullShare === null
    ? "Nulls n/a"
    : `Nulls ${formatShare(node.nullShare, locale)} (${formatCount(node.nullCount, locale)} of ${formatCount(node.valueCount, locale)})`);
  if (node.compressionRatio !== null) {
    lines.push(`Ratio ${formatNumber(node.compressionRatio, locale)}`);
  }
  const column = node.column;
  if (node.kind === "column" && column !== undefined) {
    if (column.dataType !== null) {
      lines.push(`Type ${column.dataType}`);
    }
    if (column.isPartitionColumn === true) {
      lines.push("Partition column");
    }
  }
  if (node.kind === "table" && node.sizeSource === "table") {
    lines.push("Sized from the table totals, the columns have none");
  }
  if (node.absent) {
    lines.push(node.kind === "column" && column?.isPartitionColumn === true
      ? "No totals in the file: a partition column is not stored in the files"
      : "No totals in the file");
  }
  if (node.kind === "stale") {
    lines.push("Bytes of columns that the table no longer declares");
  }
  return lines;
}

/** The legend of the fills: one swatch per bin of the share of nulls. */
function legendOf(): HTMLElement {
  return html("div", { class: "pqax-legend", "aria-label": "Share of nulls" },
    html("span", { class: "pqax-legend-title" }, "Share of nulls"),
    ...LEGEND.map(([bin, text]) => html("span", { class: "pqax-legend-item" },
      html("span", { class: "pqax-swatch", "data-bin": String(bin), "aria-hidden": "true" }), text)));
}

/**
 * Renders the column treemap of a model in a container: a breadcrumb, the tiles of the children of the focus in an
 * SVG, and the legend of the share of nulls. Each tile is an item with the key of its node; a click on a tile with
 * children moves the focus to it, a click on a leaf calls `onSelect`.
 * @param container The element that receives the view.
 * @param model The view model, from `buildModel`.
 * @param options The options of the view.
 */
export function renderColumnTreemap(container: HTMLElement, model: StorageModel, options: TreemapOptions = {}): ViewHandle<TreemapOptions> {
  let current: TreemapOptions = { ...options };
  let tree = columnTree(model);
  let sourceLabels = new Map(model.sources.map((s) => [s.key, s.label]));
  let focusKey = current.focus ?? "root";
  const hatchId = `pqax-hatch-${++instances}`;
  const withLabel = (o: TreemapOptions): TreemapOptions => ({ ...o, label: o.label ?? "Treemap of the compressed bytes by column" });

  const root = createRoot(container, "treemap", withLabel(current));
  const nav = html("nav", { class: "pqax-breadcrumb", "aria-label": "Treemap path" });
  const plot = html("div", { class: "pqax-treemap-plot" });
  const legend = legendOf();
  root.append(nav, plot, legend);
  const stopTooltip = attachTooltip(root);
  const stopKeyboard = attachKeyboard(root);
  /** The direct children of the focus, by key, as drawn last. */
  let shown = new Map<string, TreeNode>();

  const tiles = (): SVGGElement[] => [...plot.querySelectorAll<SVGGElement>("g.pqax-tile")];
  const focusTile = (key?: string) => {
    const all = tiles();
    (all.find((t) => t.getAttribute("data-key") === key) ?? all[0])?.focus();
  };

  const drawBreadcrumb = (path: readonly TreeNode[]) => {
    const parts: Node[] = [];
    path.forEach((node, i) => {
      if (i > 0) {
        parts.push(html("span", { class: "pqax-breadcrumb-sep", "aria-hidden": "true" }, "/"));
      }
      parts.push(html("button", {
        type: "button", "data-key": node.key, "aria-current": i === path.length - 1 ? "page" : null,
      }, node.label));
    });
    nav.replaceChildren(...parts);
  };

  const drawLabel = (group: SVGGElement, box: LayoutRect, text: string, size: number, baseline: number, measure: (text: string, size: number) => number, className?: string, whole = false): boolean => {
    if (box.height < baseline + 4) {
      return false;
    }
    const fitted = fit(text, box.width - 2 * PAD, size, measure, whole);
    if (fitted === null) {
      return false;
    }
    group.append(svg("text", { x: box.x + PAD, y: box.y + baseline, class: className }, fitted));
    return true;
  };

  /** Draws a node in a box: its rect, the hatch when absent. The caller adds the labels. */
  const drawShape = (group: SVGGElement, node: TreeNode, box: LayoutRect) => {
    group.setAttribute("data-kind", node.kind);
    group.setAttribute("data-bin", node.absent ? "none" : String(nullShareBin(node.nullShare)));
    if (node.absent) {
      group.classList.add("pqax-absent");
    }
    group.append(svg("rect", { x: box.x, y: box.y, width: box.width, height: box.height }));
    if (node.absent) {
      group.append(svg("rect", { class: "pqax-hatch", x: box.x, y: box.y, width: box.width, height: box.height, fill: `url(#${hatchId})` }));
    }
  };

  const absentShare = (): number => {
    const share = current.absentShare;
    return share !== undefined && Number.isFinite(share) && share >= 0 ? share : DEFAULT_ABSENT_SHARE;
  };

  const render = () => {
    const active = document.activeElement;
    const activeKey = active instanceof Element && plot.contains(active) ? active.getAttribute("data-key") : null;
    const path = focusPath(tree, focusKey);
    const focus = path[path.length - 1]!;
    focusKey = focus.key;
    root.setAttribute("data-focus", focus.key);
    shown = new Map();
    if (tree.children.length === 0) {
      nav.hidden = true;
      legend.hidden = true;
      nav.replaceChildren();
      plot.replaceChildren(html("p", { class: "pqax-empty" }, "No columns in this footprint."));
      return;
    }
    nav.hidden = false;
    legend.hidden = false;
    drawBreadcrumb(path);

    const width = Math.max(1, widthOf(root, container));
    const requested = current.height;
    const height = requested !== undefined && Number.isFinite(requested) && requested > 0 ? Math.round(requested) : DEFAULT_HEIGHT;
    const measure = measurer(root);
    const share = absentShare();
    const locale = current.locale;

    const plotSvg = svg("svg", {
      width, height, viewBox: `0 0 ${width} ${height}`, role: "group", "aria-label": `Treemap of ${focus.label}`,
    }, svg("defs", {}, svg("pattern", {
      id: hatchId, width: 6, height: 6, patternUnits: "userSpaceOnUse", patternTransform: "rotate(45)",
    }, svg("line", { class: "pqax-hatch-line", x1: 3, y1: 0, x2: 3, y2: 6 }))));

    // The tiles in the order of their weights, largest first; ties keep the order of the tree.
    const laid = squarify(weightsOf(focus, share), { x: 0, y: 0, width, height })
      .sort((a, b) => b.item.weight - a.item.weight || a.item.index - b.item.index);
    for (const { item: { node }, rect } of laid) {
      shown.set(node.key, node);
      const group = svg("g", { class: "pqax-tile" });
      makeItem(group, node.key, linesOf(node, focus, sourceLabels, locale));
      const box = inset(rect, INSET);
      drawShape(group, node, box);
      const nest = node.children.length > 0 && box.width >= NEST_WIDTH && box.height >= NEST_HEIGHT;
      if (nest) {
        group.classList.add("pqax-nested");
        drawLabel(group, box, node.label, LABEL_SIZE, 14, measure);
        const inner: LayoutRect = { x: box.x + 3, y: box.y + HEADER, width: box.width - 6, height: box.height - HEADER - 3 };
        const children = svg("g", { class: "pqax-subtiles", "aria-hidden": "true" });
        for (const sub of squarify(weightsOf(node, share), inner)) {
          if (sub.rect.width <= 0 || sub.rect.height <= 0) {
            continue;
          }
          const subGroup = svg("g", { class: "pqax-subtile" });
          drawShape(subGroup, sub.item.node, sub.rect);
          drawLabel(subGroup, sub.rect, sub.item.node.label, SMALL_SIZE, 13, measure);
          children.append(subGroup);
        }
        group.append(children);
      } else if (drawLabel(group, box, node.label, LABEL_SIZE, 16, measure) && !node.absent) {
        drawLabel(group, box, formatBytes(node.size, locale), SMALL_SIZE, 31, measure, "pqax-tile-value", true);
      }
      plotSvg.append(group);
    }
    plot.replaceChildren(plotSvg);
    if (activeKey !== null && shown.has(activeKey)) {
      focusTile(activeKey);
    }
  };

  const changeFocus = (key: string) => {
    focusKey = key;
    render();
    current.onFocusChange?.(focusKey);
  };

  const onClick = (event: MouseEvent) => {
    const target = event.target instanceof Element ? event.target : null;
    if (target === null) {
      return;
    }
    const crumb = target.closest("button[data-key]");
    if (crumb !== null && nav.contains(crumb)) {
      const key = crumb.getAttribute("data-key")!;
      if (key !== focusKey) {
        changeFocus(key);
        nav.querySelector<HTMLButtonElement>("button[aria-current]")?.focus();
      }
      return;
    }
    const tile = target.closest(".pqax-tile");
    if (tile === null || !plot.contains(tile)) {
      return;
    }
    const key = tile.getAttribute("data-key")!;
    const node = shown.get(key);
    if (node === undefined) {
      return;
    }
    if (node.children.length > 0) {
      changeFocus(key);
      focusTile();
    } else {
      current.onSelect?.(key);
    }
  };

  const onKey = (event: KeyboardEvent) => {
    if (event.key !== "Backspace" && event.key !== "Escape") {
      return;
    }
    const target = event.target instanceof Element ? event.target : null;
    if (target === null || !target.classList.contains("pqax-tile")) {
      return;
    }
    const path = focusPath(tree, focusKey);
    if (path.length < 2) {
      return;
    }
    event.preventDefault();
    const from = focusKey;
    changeFocus(path[path.length - 2]!.key);
    focusTile(from);
  };

  root.addEventListener("click", onClick);
  root.addEventListener("keydown", onKey);
  render();
  const stopObserver = observeWidth(root, () => render());

  return {
    element: root,
    update(nextModel?: StorageModel, nextOptions?: Partial<TreemapOptions>) {
      if (nextOptions !== undefined) {
        current = { ...current, ...nextOptions };
      }
      if (nextModel !== undefined) {
        tree = columnTree(nextModel);
        sourceLabels = new Map(nextModel.sources.map((s) => [s.key, s.label]));
      }
      if (nextOptions?.focus !== undefined) {
        focusKey = nextOptions.focus;
      }
      applyBaseOptions(root, withLabel(current));
      render();
    },
    dispose() {
      stopObserver();
      stopTooltip();
      stopKeyboard();
      root.removeEventListener("click", onClick);
      root.removeEventListener("keydown", onKey);
      root.remove();
    },
  };
}
