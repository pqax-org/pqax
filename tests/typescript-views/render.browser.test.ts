// The three views in the DOM of a browser: the storage stack, the column treemap, and the file strip, each mounted on
// the samples of the kit and on synthetic footprints, checked against the DOM contract of the views: the root and its
// theme, the items with their keys, focus, roles, and names, the tooltip, the keyboard, the callbacks, update and
// dispose, the empty model, a narrow container, and what each view draws. It runs in the browsers only.
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { openEmbedded, openPqax } from "@pqax/pqax";
import {
  type BaseOptions, type StorageModel, type TableModel, type TreeNode, type ViewHandle, buildModel, columnTree, findPath,
  formatBytes, formatShare, readFootprint, sortTables,
} from "@pqax/views";
import "@pqax/views/styles.css";
import { type StackOptions, renderTableStack } from "@pqax/views";
import { type TreemapOptions, nullShareBin, renderColumnTreemap } from "@pqax/views";
import { type StripOptions, renderFileStrip } from "@pqax/views";
import { SAMPLES, bytesOf, join } from "../typescript/kit";
import { EARLY_TABLE_FIELDS, syntheticFootprint } from "./synthetic";

type Kind = "stack" | "treemap" | "strip";
type Render = (container: HTMLElement, model: StorageModel, options?: BaseOptions & Record<string, unknown>) => ViewHandle<BaseOptions>;

const RENDER: Readonly<Record<Kind, Render>> = {
  stack: renderTableStack as Render,
  treemap: renderColumnTreemap as Render,
  strip: renderFileStrip as Render,
};
const KINDS: readonly Kind[] = ["stack", "treemap", "strip"];

const FILES = ["sample.pqax", "summary-only.pqax", "files.pqax", "multipart.pqax", "embedded-stored.vpax"];
/** A synthetic footprint whose fields of an early 0.1 producer, distributions, and footers are missing. */
const ABSENT = "synthetic without fields";
const SYNTHETIC = "synthetic";
/** The synthetic footprint at level 1: no footer, no column size. */
const LEVEL1 = "synthetic at level 1";
/** Two tables with an id and a name, and nothing else: every stack is empty and nothing has a size. */
const BARE = "bare tables";
const MODELS = [...FILES, SYNTHETIC, ABSENT, LEVEL1, BARE];
const models = new Map<string, StorageModel>();

const LIGHT_LIVE = "#c3c2b7";
const DARK_LIVE = "#5f5e59";
const SEGMENTS = ["live", "nonLive", "deleted", "metadata", "stale"];

beforeAll(async () => {
  for (const file of FILES) {
    const bytes = bytesOf(join(SAMPLES, file));
    const doc = file.endsWith(".vpax") ? await openEmbedded(bytes) : await openPqax(bytes);
    models.set(file, buildModel(await readFootprint(doc)));
  }
  models.set(SYNTHETIC, buildModel(syntheticFootprint().data));
  models.set(ABSENT, buildModel(syntheticFootprint({
    tables: 6, columns: 30, files: 60, withFiles: false, dropTableFields: [...EARLY_TABLE_FIELDS, "fileSizes", "footerSize"],
  }).data));
  models.set(LEVEL1, buildModel(syntheticFootprint({ level: 1, tables: 8, columns: 60, files: 200 }).data));
  models.set(BARE, buildModel({ summary: { tables: { length: 2, id: [1, 2], name: ["table_001", "table_002"] } } }));
});

const modelOf = (name: string): StorageModel => models.get(name)!;

let container: HTMLElement;
let handles: ViewHandle<BaseOptions>[] = [];

beforeEach(() => {
  container = document.createElement("div");
  container.style.width = "800px";
  document.body.append(container);
});

afterEach(() => {
  for (const handle of handles) {
    handle.dispose();
  }
  handles = [];
  container.remove();
  document.querySelectorAll("[data-test-ancestor]").forEach((element) => element.remove());
});

function mount(kind: Kind, model: StorageModel, options: BaseOptions & Record<string, unknown> = {}, into: HTMLElement = container): ViewHandle<BaseOptions> {
  const handle = RENDER[kind](into, model, options);
  handles.push(handle);
  return handle;
}

/** Keeps a handle to dispose it after the test. */
function track<O>(handle: ViewHandle<O>): ViewHandle<O> {
  handles.push(handle as unknown as ViewHandle<BaseOptions>);
  return handle;
}

const stack = (model: StorageModel, options: StackOptions = {}) => track(renderTableStack(container, model, options));
const treemap = (model: StorageModel, options: TreemapOptions = {}) => track(renderColumnTreemap(container, model, options));
const strip = (model: StorageModel, options: StripOptions = {}) => track(renderFileStrip(container, model, options));

const items = (root: Element) => [...root.querySelectorAll<HTMLElement | SVGElement>(".pqax-item")];
const tooltipOf = (root: Element) => root.querySelector<HTMLElement>("div.pqax-tooltip[role=tooltip]")!;
const num = (element: Element, name: string) => Number.parseFloat(element.getAttribute(name)!);
const click = (element: Element) => element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
const key = (element: Element, value: string) => element.dispatchEvent(new KeyboardEvent("keydown", { key: value, bubbles: true, cancelable: true }));

/** The label of an item, the first line of its tooltip: a table, or a node of the tree. */
function labelOf(kind: Kind, model: StorageModel, itemKey: string): string {
  if (kind === "treemap") {
    return findPath(columnTree(model), itemKey)!.at(-1)!.label;
  }
  return model.tables.find((t) => t.key === itemKey)!.label;
}

function component(table: TableModel, k: string) {
  return table.stack.components.find((c) => c.key === k)!;
}

/** The order of the strip: median file size, largest first, the unknown last, then label, then row. */
function stripOrder(tables: readonly TableModel[], order: "size" | "count" | "label" = "size"): string[] {
  const byLabel = (a: TableModel, b: TableModel) => (a.label < b.label ? -1 : a.label > b.label ? 1 : 0);
  const desc = (a: number | null, b: number | null) => (a === null ? (b === null ? 0 : 1) : b === null ? -1 : b - a);
  return [...tables].sort((a, b) => {
    if (order === "label") {
      return byLabel(a, b) || a.row - b.row;
    }
    if (order === "count") {
      return desc(a.files.count, b.files.count) || a.row - b.row;
    }
    return desc(a.files.median, b.files.median) || byLabel(a, b) || a.row - b.row;
  }).map((t) => t.key);
}

describe.each(KINDS)("the %s view", (kind) => {
  it.each(MODELS)("on %s, keeps the common contract of the root, the items, and the tooltip", (name) => {
    const model = modelOf(name);
    const handle = mount(kind, model);
    const root = handle.element;
    expect(container.children.length).toBe(1);
    expect(container.firstElementChild).toBe(root);
    expect(root.tagName).toBe("DIV");
    expect(root.classList.contains("pqax-view")).toBe(true);
    expect(root.classList.contains(`pqax-${kind}`)).toBe(true);
    expect(root.hasAttribute("data-theme")).toBe(false);
    const all = items(root);
    expect(all.length).toBeGreaterThan(0);
    for (const item of all) {
      const itemKey = item.getAttribute("data-key")!;
      expect(itemKey).toBeTruthy();
      expect(item.getAttribute("tabindex")).toBe("0");
      expect(item.getAttribute("role")).toBeTruthy();
      const label = item.getAttribute("aria-label")!;
      expect(label.startsWith(labelOf(kind, model, itemKey))).toBe(true);
    }
    expect(new Set(all.map((item) => item.getAttribute("data-key"))).size).toBe(all.length);
    expect(root.querySelectorAll("div.pqax-tooltip").length).toBe(1);
    expect(tooltipOf(root).hidden).toBe(true);
  });

  it("removes its root on dispose, and leaves the container empty", () => {
    const handle = RENDER[kind](container, modelOf("sample.pqax"));
    expect(container.children.length).toBe(1);
    handle.dispose();
    expect(handle.element.isConnected).toBe(false);
    expect(container.childNodes.length).toBe(0);
    handle.dispose();
    expect(container.childNodes.length).toBe(0);
  });

  it("renders p.pqax-empty for an empty model, and draws again on update with a model", () => {
    const empty = buildModel({ summary: {} });
    const handle = mount(kind, empty);
    expect(handle.element.querySelector("p.pqax-empty")).not.toBeNull();
    expect(items(handle.element)).toHaveLength(0);
    handle.update(modelOf("sample.pqax"));
    expect(handle.element.querySelector("p.pqax-empty")).toBeNull();
    expect(items(handle.element).length).toBeGreaterThan(0);
    expect(container.children.length).toBe(1);
    handle.update(empty);
    expect(handle.element.querySelector("p.pqax-empty")).not.toBeNull();
  });

  it("calls onSelect with the key of an item on a click, on Enter, and on Space", () => {
    const onSelect = vi.fn();
    const model = modelOf("sample.pqax");
    // In the treemap, a leaf calls onSelect: the columns of sales are leaves.
    const handle = mount(kind, model, kind === "treemap" ? { onSelect, focus: "t:1" } : { onSelect });
    const [first, second] = items(handle.element);
    const firstKey = first!.getAttribute("data-key")!;
    click(first!);
    expect(onSelect).toHaveBeenLastCalledWith(firstKey);
    key(second!, "Enter");
    expect(onSelect).toHaveBeenLastCalledWith(second!.getAttribute("data-key"));
    key(first!, " ");
    expect(onSelect).toHaveBeenLastCalledWith(firstKey);
    expect(onSelect).toHaveBeenCalledTimes(3);
  });

  it("moves the focus to the next item with ArrowDown and back with ArrowUp", () => {
    const handle = mount(kind, modelOf(SYNTHETIC));
    const all = items(handle.element);
    expect(all.length).toBeGreaterThan(2);
    all[0]!.focus();
    expect(document.activeElement).toBe(all[0]);
    key(all[0]!, "ArrowDown");
    expect(document.activeElement).toBe(all[1]);
    key(all[1]!, "ArrowDown");
    expect(document.activeElement).toBe(all[2]);
    key(all[2]!, "ArrowUp");
    expect(document.activeElement).toBe(all[1]);
    key(all[1]!, "End");
    expect(document.activeElement).toBe(all.at(-1));
    key(all.at(-1)!, "Home");
    expect(document.activeElement).toBe(all[0]);
  });

  it("shows the tooltip on focus, with the first line of the item, and hides it on blur", () => {
    const model = modelOf("sample.pqax");
    const handle = mount(kind, model);
    const root = handle.element;
    const tip = tooltipOf(root);
    const [first, second] = items(root);
    first!.focus();
    expect(tip.hidden).toBe(false);
    expect(tip.querySelector("strong")!.textContent).toBe(labelOf(kind, model, first!.getAttribute("data-key")!));
    expect(first!.getAttribute("aria-label")!.split(". ")[0]).toBe(tip.querySelector("strong")!.textContent);
    expect(getComputedStyle(tip).display).not.toBe("none");
    second!.focus();
    expect(tip.querySelector("strong")!.textContent).toBe(labelOf(kind, model, second!.getAttribute("data-key")!));
    second!.blur();
    expect(tip.hidden).toBe(true);
  });

  it("shows the tooltip when the pointer moves over an item", () => {
    const handle = mount(kind, modelOf("sample.pqax"));
    const [first] = items(handle.element);
    const box = first!.getBoundingClientRect();
    first!.dispatchEvent(new PointerEvent("pointermove", { bubbles: true, clientX: box.left + 2, clientY: box.top + 2 }));
    expect(tooltipOf(handle.element).hidden).toBe(false);
    handle.element.dispatchEvent(new PointerEvent("pointerleave"));
    expect(tooltipOf(handle.element).hidden).toBe(true);
  });

  it("takes the dark and light values of styles.css from the theme option, and from an ancestor", () => {
    const live = (root: HTMLElement) => getComputedStyle(root).getPropertyValue("--pqax-live").trim().toLowerCase();
    const dark = mount(kind, modelOf("sample.pqax"), { theme: "dark" });
    expect(dark.element.getAttribute("data-theme")).toBe("dark");
    expect(live(dark.element)).toBe(DARK_LIVE);
    const light = mount(kind, modelOf("sample.pqax"), { theme: "light" });
    expect(light.element.getAttribute("data-theme")).toBe("light");
    expect(live(light.element)).toBe(LIGHT_LIVE);
    const auto = mount(kind, modelOf("sample.pqax"), { theme: "auto" });
    expect(auto.element.hasAttribute("data-theme")).toBe(false);

    const ancestor = document.createElement("div");
    ancestor.setAttribute("data-theme", "dark");
    ancestor.setAttribute("data-test-ancestor", "");
    ancestor.style.width = "800px";
    document.body.append(ancestor);
    const inherited = mount(kind, modelOf("sample.pqax"), {}, ancestor);
    expect(inherited.element.hasAttribute("data-theme")).toBe(false);
    expect(live(inherited.element)).toBe(DARK_LIVE);
    // A forced theme wins over the ancestor.
    const forced = mount(kind, modelOf("sample.pqax"), { theme: "light" }, ancestor);
    expect(live(forced.element)).toBe(LIGHT_LIVE);
    // The theme changes with update.
    dark.update(undefined, { theme: "light" });
    expect(dark.element.getAttribute("data-theme")).toBe("light");
    expect(live(dark.element)).toBe(LIGHT_LIVE);
    dark.update(undefined, { theme: "auto" });
    expect(dark.element.hasAttribute("data-theme")).toBe(false);
  });

  it.each(["sample.pqax", SYNTHETIC, ABSENT, BARE])("renders %s in a container of 340 pixels without horizontal overflow", (name) => {
    container.style.width = "340px";
    const handle = mount(kind, modelOf(name));
    const root = handle.element;
    root.getBoundingClientRect();
    expect(root.clientWidth).toBeLessThanOrEqual(340);
    expect(root.scrollWidth).toBeLessThanOrEqual(root.clientWidth + 1);
  });

  it("sets the accessible name of the root from the label option", () => {
    const handle = mount(kind, modelOf("sample.pqax"), { label: "Footprint of source_a" });
    expect(handle.element.getAttribute("aria-label")).toBe("Footprint of source_a");
  });
});

describe("the storage stack", () => {
  const rowsOf = (root: Element) => [...root.querySelectorAll<HTMLElement>("div.pqax-row.pqax-item")];
  const widths = (row: Element) => [...row.querySelectorAll<HTMLElement>(".pqax-bar > span.pqax-segment")]
    .map((segment) => Number.parseFloat(segment.style.width));
  const sum = (values: number[]) => values.reduce((a, b) => a + b, 0);

  it.each(MODELS)("on %s, draws the toolbar, the legend, and one row per table in the order of waste", (name) => {
    const model = modelOf(name);
    const root = stack(model).element;
    expect(root.getAttribute("data-mode")).toBe("bytes");
    const buttons = root.querySelectorAll(".pqax-toolbar .pqax-toggle button");
    expect([...buttons].map((b) => [b.getAttribute("data-mode"), b.getAttribute("aria-pressed")])).toEqual([["bytes", "true"], ["shares", "false"]]);
    const legend = root.querySelector(".pqax-legend")!;
    expect([...legend.querySelectorAll(".pqax-legend-item[data-component]")].map((e) => e.getAttribute("data-component"))).toEqual(SEGMENTS);
    expect(legend.querySelectorAll(".pqax-legend-item .pqax-absent-mark").length).toBe(1);
    const rows = rowsOf(root);
    expect(rows.map((r) => r.getAttribute("data-key"))).toEqual(sortTables(model.tables, "waste").map((t) => t.key));
    expect(items(root)).toEqual(rows);
  });

  it.each(MODELS)("on %s, draws the segments with bytes, the marks of the absent components, and the total", (name) => {
    const model = modelOf(name);
    const root = stack(model).element;
    const scale = Math.max(...model.tables.map((t) => t.stack.total));
    for (const row of rowsOf(root)) {
      const table = model.tables.find((t) => t.key === row.getAttribute("data-key"))!;
      expect(row.querySelector(".pqax-label")!.textContent).toContain(table.label);
      expect(row.querySelector(".pqax-bar")).not.toBeNull();
      const bytes: Record<string, number> = {
        live: component(table, "live").bytes, nonLive: component(table, "nonLive").bytes, deleted: component(table, "deleted").bytes,
        metadata: component(table, "metadata").bytes + component(table, "footers").bytes, stale: component(table, "stale").bytes,
      };
      const segments = [...row.querySelectorAll(".pqax-bar > span.pqax-segment")].map((s) => s.getAttribute("data-component"));
      expect(segments).toEqual(SEGMENTS.filter((k) => bytes[k]! > 0));
      expect(sum(widths(row))).toBeCloseTo(scale > 0 ? (table.stack.total / scale) * 100 : 0, 1);
      const absent = SEGMENTS.filter((k) => (k === "metadata"
        ? component(table, "metadata").absent || component(table, "footers").absent
        : component(table, k).absent));
      expect([...row.querySelectorAll("span.pqax-absent[data-component]")].map((m) => m.getAttribute("data-component"))).toEqual(absent);
      expect(row.querySelector("span.pqax-value")!.textContent).toBe(formatBytes(table.stack.total));
    }
    const largest = sortTables(model.tables, "total")[0]!;
    expect(sum(widths(root.querySelector(`.pqax-row[data-key="${largest.key}"]`)!))).toBeCloseTo(scale > 0 ? 100 : 0, 1);
  });

  it("draws absent marks for the components that the file does not have", () => {
    const root = stack(modelOf(ABSENT)).element;
    for (const row of rowsOf(root)) {
      expect([...row.querySelectorAll("span.pqax-absent")].map((m) => m.getAttribute("data-component"))).toEqual(["nonLive", "metadata", "stale"]);
      expect([...row.querySelectorAll(".pqax-segment")].map((m) => m.getAttribute("data-component"))).not.toContain("metadata");
    }
    // At level 1 the footers are missing but the metadata is there: the metadata segment is drawn, and marked.
    const level1 = stack(modelOf(LEVEL1)).element;
    for (const row of rowsOf(level1)) {
      expect([...row.querySelectorAll("span.pqax-absent")].map((m) => m.getAttribute("data-component"))).toEqual(["metadata", "stale"]);
      expect([...row.querySelectorAll(".pqax-segment")].map((m) => m.getAttribute("data-component"))).toContain("metadata");
    }
    // Without any field of the stack, every component is marked and no segment is drawn.
    const bare = stack(modelOf(BARE)).element;
    for (const row of rowsOf(bare)) {
      expect(row.querySelectorAll(".pqax-segment")).toHaveLength(0);
      expect([...row.querySelectorAll("span.pqax-absent")].map((m) => m.getAttribute("data-component"))).toEqual(SEGMENTS);
    }
  });

  it("switches to shares with the toggle: data-mode, aria-pressed, onModeChange, bars of 100%, and the waste share", () => {
    const onModeChange = vi.fn();
    const model = modelOf(SYNTHETIC);
    const root = stack(model, { onModeChange }).element;
    click(root.querySelector('.pqax-toggle button[data-mode="shares"]')!);
    expect(root.getAttribute("data-mode")).toBe("shares");
    expect(onModeChange).toHaveBeenCalledWith("shares");
    expect(root.querySelector('.pqax-toggle button[data-mode="shares"]')!.getAttribute("aria-pressed")).toBe("true");
    expect(root.querySelector('.pqax-toggle button[data-mode="bytes"]')!.getAttribute("aria-pressed")).toBe("false");
    for (const row of rowsOf(root)) {
      const table = model.tables.find((t) => t.key === row.getAttribute("data-key"))!;
      expect(sum(widths(row))).toBeCloseTo(100, 1);
      expect(row.querySelector("span.pqax-value")!.textContent).toBe(`${formatShare(table.stack.wasteShare)} waste`);
    }
    click(root.querySelector('.pqax-toggle button[data-mode="shares"]')!);
    expect(onModeChange).toHaveBeenCalledTimes(1);
    click(root.querySelector('.pqax-toggle button[data-mode="bytes"]')!);
    expect(root.getAttribute("data-mode")).toBe("bytes");
    expect(onModeChange).toHaveBeenLastCalledWith("bytes");
  });

  it("takes the mode, the order, the toggle, and the most tables from the options and from update", () => {
    const model = modelOf(SYNTHETIC);
    const handle = stack(model, { mode: "shares", showToggle: false, maxTables: 5 });
    const root = handle.element;
    expect(root.getAttribute("data-mode")).toBe("shares");
    expect(root.querySelector(".pqax-toggle")).toBeNull();
    expect(rowsOf(root).map((r) => r.getAttribute("data-key"))).toEqual(sortTables(model.tables, "waste").slice(0, 5).map((t) => t.key));
    handle.update(undefined, { mode: "bytes", order: "label", maxTables: undefined, showToggle: true });
    expect(root.getAttribute("data-mode")).toBe("bytes");
    expect(root.querySelector(".pqax-toggle")).not.toBeNull();
    expect(rowsOf(root).map((r) => r.getAttribute("data-key"))).toEqual(sortTables(model.tables, "label").map((t) => t.key));
    handle.update(modelOf("sample.pqax"));
    expect(rowsOf(root).map((r) => r.getAttribute("data-key"))).toEqual(["t:2", "t:1"]);
    expect(container.children.length).toBe(1);
  });
});

describe("the column treemap", () => {
  const tilesOf = (root: Element) => [...root.querySelectorAll<SVGGElement>("g.pqax-item.pqax-tile")];
  const crumbs = (root: Element) => [...root.querySelectorAll<HTMLButtonElement>("nav.pqax-breadcrumb button[data-key]")];
  const tile = (root: Element, k: string) => root.querySelector<SVGGElement>(`g.pqax-tile[data-key="${k}"]`)!;

  /** The tiles of the focus against the tree: one per child, the rect first, the bin, absent marks, and the areas. */
  function expectTiles(root: HTMLElement, model: StorageModel, focusKey: string, absentShare = 0.01): void {
    const focus: TreeNode = findPath(columnTree(model), focusKey)!.at(-1)!;
    const tiles = tilesOf(root);
    expect(root.getAttribute("data-focus")).toBe(focusKey);
    expect(tiles.map((t) => t.getAttribute("data-key")).sort()).toEqual(focus.children.map((n) => n.key).sort());
    expect(items(root)).toEqual(tiles);
    const svgElement = root.querySelector("svg")!;
    const plotArea = num(svgElement, "width") * num(svgElement, "height");
    const weight = (n: TreeNode) => (focus.size > 0 ? (n.absent ? focus.size * absentShare : n.size) : 1);
    const total = focus.children.reduce((s, n) => s + weight(n), 0);
    for (const element of tiles) {
      const node = focus.children.find((n) => n.key === element.getAttribute("data-key"))!;
      expect(element.getAttribute("data-kind")).toBe(node.kind);
      expect(element.firstElementChild!.tagName.toLowerCase()).toBe("rect");
      if (node.absent) {
        expect(element.classList.contains("pqax-absent")).toBe(true);
        expect(element.getAttribute("data-bin")).toBe("none");
      } else {
        expect(element.classList.contains("pqax-absent")).toBe(false);
        expect(element.getAttribute("data-bin")).toBe(String(nullShareBin(node.nullShare)));
      }
      // The area of the rect is its share of the plot, less the gap of a pixel around it.
      const rect = element.firstElementChild!;
      const w = num(rect, "width");
      const h = num(rect, "height");
      const expected = (weight(node) / total) * plotArea;
      if (w > 0 && h > 0) {
        expect(Math.abs(expected - w * h)).toBeLessThanOrEqual(2 * (w + h) + 4 + expected * 1e-6);
      } else {
        expect(expected).toBeLessThan(2 * Math.sqrt(plotArea) + 4);
      }
    }
  }

  it.each(MODELS)("on %s, draws one tile per source at the root, the breadcrumb, and the legend of the bins", (name) => {
    const model = modelOf(name);
    const root = treemap(model).element;
    expectTiles(root, model, "root");
    expect(crumbs(root).map((b) => [b.getAttribute("data-key"), b.getAttribute("aria-current")])).toEqual([["root", "page"]]);
    const legend = root.querySelector(".pqax-legend")!;
    const legendItems = [...legend.querySelectorAll(".pqax-legend-item")];
    expect(legendItems).toHaveLength(7);
    expect(legendItems.map((e) => e.querySelector(".pqax-swatch[data-bin]")!.getAttribute("data-bin"))).toEqual(["0", "1", "2", "3", "4", "5", "none"]);
    expect(root.querySelectorAll("svg").length).toBeGreaterThan(0);
    expect(num(root.querySelector("svg")!, "height")).toBe(420);
  });

  it("drills from the root of sample.pqax to the lakehouse and to sales, and back with the breadcrumb", () => {
    const onFocusChange = vi.fn();
    const onSelect = vi.fn();
    const model = modelOf("sample.pqax");
    const root = treemap(model, { onFocusChange, onSelect }).element;
    expect(tilesOf(root)).toHaveLength(2);
    click(tile(root, "s:1"));
    expect(onFocusChange).toHaveBeenLastCalledWith("s:1");
    expect(onSelect).not.toHaveBeenCalled();
    expectTiles(root, model, "s:1");
    expect(crumbs(root).map((b) => b.getAttribute("data-key"))).toEqual(["root", "s:1"]);
    click(tile(root, "t:1"));
    expect(onFocusChange).toHaveBeenLastCalledWith("t:1");
    expectTiles(root, model, "t:1");
    const tiles = tilesOf(root);
    expect(tiles).toHaveLength(5);
    const absent = tiles.filter((t) => t.classList.contains("pqax-absent"));
    expect(absent.map((t) => [t.getAttribute("data-key"), t.getAttribute("data-bin")])).toEqual([["c:5", "none"]]);
    expect(absent[0]!.getAttribute("aria-label")!.startsWith("Year")).toBe(true);
    const path = crumbs(root);
    expect(path.map((b) => b.getAttribute("data-key"))).toEqual(["root", "s:1", "t:1"]);
    expect(path.map((b) => b.getAttribute("aria-current"))).toEqual([null, null, "page"]);

    // A leaf calls onSelect and does not drill.
    click(tile(root, "c:4"));
    expect(onSelect).toHaveBeenCalledWith("c:4");
    expect(root.getAttribute("data-focus")).toBe("t:1");
    expect(onFocusChange).toHaveBeenCalledTimes(2);

    click(crumbs(root)[1]!);
    expect(root.getAttribute("data-focus")).toBe("s:1");
    expect(onFocusChange).toHaveBeenLastCalledWith("s:1");
    click(crumbs(root)[0]!);
    expectTiles(root, model, "root");
    expect(crumbs(root)).toHaveLength(1);
  });

  it("goes up with Backspace and Escape on a tile", () => {
    const onFocusChange = vi.fn();
    const root = treemap(modelOf("sample.pqax"), { focus: "t:1", onFocusChange }).element;
    expect(root.getAttribute("data-focus")).toBe("t:1");
    key(tilesOf(root)[0]!, "Backspace");
    expect(root.getAttribute("data-focus")).toBe("s:1");
    expect(onFocusChange).toHaveBeenLastCalledWith("s:1");
    key(tilesOf(root)[0]!, "Escape");
    expect(root.getAttribute("data-focus")).toBe("root");
    key(tilesOf(root)[0]!, "Escape");
    expect(root.getAttribute("data-focus")).toBe("root");
    expect(onFocusChange).toHaveBeenCalledTimes(2);
  });

  it("drills with Enter on a tile that has children", () => {
    const root = treemap(modelOf("sample.pqax")).element;
    key(tile(root, "s:2"), "Enter");
    expect(root.getAttribute("data-focus")).toBe("s:2");
  });

  it("takes the focus, the height, and the absent share from the options and from update", () => {
    const model = modelOf("sample.pqax");
    const handle = treemap(model, { focus: "t:1", height: 300, absentShare: 0.2 });
    const root = handle.element;
    expect(num(root.querySelector("svg")!, "height")).toBe(300);
    expectTiles(root, model, "t:1", 0.2);
    handle.update(undefined, { focus: "t:2", absentShare: 0.05 });
    expectTiles(root, model, "t:2", 0.05);
    handle.update(undefined, { focus: "no such key" });
    expect(root.getAttribute("data-focus")).toBe("root");
    handle.update(modelOf(SYNTHETIC), { focus: "s:2" });
    expectTiles(root, modelOf(SYNTHETIC), "s:2");
  });

  it("draws every tile of a collection at level 1 as absent, with equal weights", () => {
    const model = modelOf(LEVEL1);
    const root = treemap(model).element;
    expectTiles(root, model, "root");
    expect(tilesOf(root).every((t) => t.classList.contains("pqax-absent") && t.getAttribute("data-bin") === "none")).toBe(true);
    click(tilesOf(root)[0]!);
    expect(root.getAttribute("data-focus")).not.toBe("root");
    expectTiles(root, model, root.getAttribute("data-focus")!);
  });

  it("draws the tables of the synthetic footprint with their stale leaf and their absent partition column", () => {
    const model = modelOf(SYNTHETIC);
    const tree = columnTree(model);
    const tables = tree.children.flatMap((s) => s.children);
    const stale = tables.find((t) => t.children.some((c) => c.kind === "stale"))!;
    const partitioned = tables.find((t) => t.children.some((c) => c.absent))!;
    const root = treemap(model, { focus: stale.key }).element;
    expectTiles(root, model, stale.key);
    expect(tile(root, `${stale.key}/stale`).getAttribute("data-kind")).toBe("stale");
    const handle = treemap(model, { focus: partitioned.key });
    expectTiles(handle.element, model, partitioned.key);
    expect(handle.element.querySelectorAll("g.pqax-tile.pqax-absent").length).toBe(1);
  });
});

describe("the file strip", () => {
  const rowsOf = (root: Element) => [...root.querySelectorAll<SVGGElement>("g.pqax-item.pqax-strip-row")];
  const x1 = (e: Element | null) => num(e!, "x1");

  it.each(MODELS)("on %s, draws one row per table by median size, with the box plot and the V-Order column", (name) => {
    const model = modelOf(name);
    const root = strip(model).element;
    expect(root.querySelectorAll("svg")).toHaveLength(1);
    const rows = rowsOf(root);
    expect(rows.map((r) => r.getAttribute("data-key"))).toEqual(stripOrder(model.tables));
    expect(items(root)).toEqual(rows);
    for (const row of rows) {
      const table = model.tables.find((t) => t.key === row.getAttribute("data-key"))!;
      const f = table.files;
      expect(row.firstElementChild!.matches("rect.pqax-hit")).toBe(true);
      const dots = row.querySelectorAll("circle.pqax-dot").length;
      expect(dots).toBe(f.sizes === null ? 0 : Math.min(400, f.sizes.length));
      if (f.source === null) {
        expect(row.querySelector("text.pqax-absent:not([data-component])")).not.toBeNull();
        expect(row.querySelector("line.pqax-median")).toBeNull();
      } else {
        expect(row.querySelector("text.pqax-absent:not([data-component])")).toBeNull();
        const range = row.querySelector("line.pqax-range")!;
        const box = row.querySelector("rect.pqax-box")!;
        const median = row.querySelector("line.pqax-median")!;
        const boxLeft = num(box, "x");
        const boxRight = boxLeft + num(box, "width");
        // min <= q1 <= median <= q3 <= max in x, on the log scale.
        expect(x1(range)).toBeLessThanOrEqual(boxLeft + 1e-6);
        expect(boxLeft).toBeLessThanOrEqual(x1(median) + 1e-6);
        expect(x1(median)).toBeLessThanOrEqual(boxRight + 1e-6);
        expect(boxRight).toBeLessThanOrEqual(num(range, "x2") + 1);
        if (f.minimum! < f.maximum!) {
          expect(num(range, "x2")).toBeGreaterThan(x1(range));
        }
        if (f.firstQuartile! < f.median! && f.median! < f.thirdQuartile!) {
          expect(boxLeft).toBeLessThan(x1(median));
          expect(x1(median)).toBeLessThan(boxRight);
        }
        for (const dot of row.querySelectorAll("circle.pqax-dot")) {
          expect(num(dot, "cx")).toBeGreaterThanOrEqual(x1(range) - 1e-6);
          expect(num(dot, "cx")).toBeLessThanOrEqual(num(range, "x2") + 1e-6);
        }
      }
      if (f.vOrder === null) {
        expect(row.querySelector('text.pqax-absent[data-component="vOrder"]')).not.toBeNull();
        expect(row.querySelector(".pqax-vorder-fill")).toBeNull();
      } else {
        expect(row.querySelector(".pqax-vorder-track")).not.toBeNull();
        const fill = row.querySelector(".pqax-vorder-fill")!;
        expect(num(fill, "width") / num(row.querySelector(".pqax-vorder-track")!, "width")).toBeCloseTo(f.vOrder.share, 6);
        expect(row.querySelector("text.pqax-vorder-value")!.textContent).toBe(formatShare(f.vOrder.share));
      }
    }
  });

  it("draws 3 dots for sales and 2 for orders of sample.pqax, and none for summary-only.pqax", () => {
    const sample = strip(modelOf("sample.pqax")).element;
    expect(sample.querySelectorAll('g[data-key="t:1"] circle.pqax-dot')).toHaveLength(3);
    expect(sample.querySelectorAll('g[data-key="t:2"] circle.pqax-dot')).toHaveLength(2);
    const summaryOnly = strip(modelOf("summary-only.pqax")).element;
    expect(summaryOnly.querySelectorAll("circle.pqax-dot")).toHaveLength(0);
    expect(rowsOf(summaryOnly).every((row) => row.querySelector("rect.pqax-box") !== null)).toBe(true);
  });

  it("puts the dots of the files at the x of their sizes, in the order of the sizes", () => {
    const model = modelOf("sample.pqax");
    const root = strip(model).element;
    const row = root.querySelector('g[data-key="t:1"]')!;
    const xs = [...row.querySelectorAll("circle.pqax-dot")].map((d) => num(d, "cx"));
    expect([...xs].sort((a, b) => a - b)).toEqual(xs);
    // The sizes are 28,252, 47,820, and 83,049: the first quartile, the median, and the maximum.
    expect(xs[0]).toBeCloseTo(num(row.querySelector("rect.pqax-box")!, "x"), 6);
    expect(xs[1]).toBeCloseTo(x1(row.querySelector("line.pqax-median")), 6);
    expect(xs[2]).toBeCloseTo(num(row.querySelector("line.pqax-range")!, "x2"), 6);
  });

  it("draws the threshold line only when the threshold is in the domain", () => {
    // The files of sample.pqax are below 100 KB, far from the default threshold of 128 MiB.
    expect(modelOf("sample.pqax").tables[0]!.files.smallFiles.threshold).toBe(128 * 1024 * 1024);
    expect(strip(modelOf("sample.pqax")).element.querySelector("line.pqax-threshold")).toBeNull();
    expect(strip(modelOf(SYNTHETIC)).element.querySelector("line.pqax-threshold")).not.toBeNull();
    const handle = strip(modelOf(SYNTHETIC), { showThreshold: false });
    expect(handle.element.querySelector("line.pqax-threshold")).toBeNull();
  });

  it("draws the threshold of a model built with a smaller threshold between the sizes of the files", async () => {
    const doc = await openPqax(bytesOf(join(SAMPLES, "sample.pqax")));
    const model = buildModel(await readFootprint(doc), { smallFileSize: 40000 });
    const root = strip(model).element;
    const line = root.querySelector("line.pqax-threshold")!;
    expect(line).not.toBeNull();
    const row = root.querySelector('g[data-key="t:1"]')!;
    const [small, medium] = [...row.querySelectorAll("circle.pqax-dot")].map((d) => num(d, "cx"));
    expect(x1(line)).toBeGreaterThan(small!);
    expect(x1(line)).toBeLessThan(medium!);
  });

  it("takes the order, the dots, and the row height from the options and from update", () => {
    const model = modelOf(SYNTHETIC);
    const handle = strip(model, { order: "count", maxDots: 10 });
    const root = handle.element;
    expect(rowsOf(root).map((r) => r.getAttribute("data-key"))).toEqual(stripOrder(model.tables, "count"));
    expect(rowsOf(root).every((row) => row.querySelectorAll("circle.pqax-dot").length <= 10)).toBe(true);
    handle.update(undefined, { order: "label", showFiles: false });
    expect(rowsOf(root).map((r) => r.getAttribute("data-key"))).toEqual(stripOrder(model.tables, "label"));
    expect(root.querySelectorAll("circle.pqax-dot")).toHaveLength(0);
    const before = num(root.querySelector("svg")!, "height");
    handle.update(undefined, { rowHeight: 56 });
    expect(num(root.querySelector("svg")!, "height")).toBeGreaterThan(before);
    handle.update(modelOf("sample.pqax"));
    expect(rowsOf(root)).toHaveLength(2);
    expect(container.children.length).toBe(1);
  });
});
