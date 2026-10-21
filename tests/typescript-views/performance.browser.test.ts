// The time that the views take on the synthetic footprint of 1,000 columns and 5,000 files: the model, then each view
// rendered in a container of 800 pixels with its first layout, forced with getBoundingClientRect, and the treemap
// focused on the table with the most columns. The test prints the times, with the engine, and fails only when a view
// takes 2 seconds or more. It runs in the browsers only.
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { type BaseOptions, type StorageModel, type ViewHandle, buildModel, columnTree } from "@pqax/views";
import "@pqax/views/styles.css";
import { renderTableStack } from "@pqax/views";
import { renderColumnTreemap } from "@pqax/views";
import { renderFileStrip } from "@pqax/views";
import { syntheticFootprint } from "./synthetic";

const BOUND_MS = 2000;
const ENGINE = /Firefox\//.test(navigator.userAgent) ? "firefox" : /Chrome\//.test(navigator.userAgent) ? "chromium" : "webkit";
const log = (text: string) => console.log(`[${ENGINE}] ${text}`);

let container: HTMLElement;
let handle: ViewHandle<BaseOptions> | null = null;

beforeEach(() => {
  container = document.createElement("div");
  container.style.width = "800px";
  document.body.append(container);
});

afterEach(() => {
  handle?.dispose();
  handle = null;
  container.remove();
});

/** Renders a view and forces its first layout, and returns the time in milliseconds. */
function time(render: () => ViewHandle<BaseOptions>): number {
  const start = performance.now();
  handle = render();
  handle.element.getBoundingClientRect();
  void handle.element.offsetHeight;
  return performance.now() - start;
}

describe("the views on 1,000 columns and 5,000 files", () => {
  const data = syntheticFootprint().data;
  let model: StorageModel;

  it("builds the model", () => {
    const start = performance.now();
    model = buildModel(data);
    const elapsed = performance.now() - start;
    log(`buildModel: ${elapsed.toFixed(1)} ms`);
    expect(model.columns).toHaveLength(1000);
    expect(model.collection.totals.files).toBe(5000);
    expect(elapsed).toBeLessThan(BOUND_MS);
  });

  it("renders the storage stack", () => {
    const elapsed = time(() => renderTableStack(container, model ?? buildModel(data)) as ViewHandle<BaseOptions>);
    log(`renderTableStack: ${elapsed.toFixed(1)} ms, ${handle!.element.querySelectorAll(".pqax-segment").length} segments`);
    expect(handle!.element.querySelectorAll(".pqax-row")).toHaveLength(40);
    expect(elapsed).toBeLessThan(BOUND_MS);
  });

  it("renders the column treemap at the root", () => {
    const elapsed = time(() => renderColumnTreemap(container, model ?? buildModel(data)) as ViewHandle<BaseOptions>);
    log(`renderColumnTreemap at the root: ${elapsed.toFixed(1)} ms, ${handle!.element.querySelectorAll("rect").length} rects`);
    expect(handle!.element.querySelectorAll("g.pqax-tile")).toHaveLength(3);
    expect(elapsed).toBeLessThan(BOUND_MS);
  });

  it("renders the column treemap focused on the table with the most columns", () => {
    const m = model ?? buildModel(data);
    const widest = columnTree(m).children.flatMap((s) => s.children).reduce((a, b) => (b.children.length > a.children.length ? b : a));
    const elapsed = time(() => renderColumnTreemap(container, m, { focus: widest.key }) as ViewHandle<BaseOptions>);
    log(`renderColumnTreemap on ${widest.label}, ${widest.children.length} columns: ${elapsed.toFixed(1)} ms`);
    expect(handle!.element.getAttribute("data-focus")).toBe(widest.key);
    expect(handle!.element.querySelectorAll("g.pqax-tile")).toHaveLength(widest.children.length);
    expect(elapsed).toBeLessThan(BOUND_MS);
  });

  it("renders the file strip", () => {
    const elapsed = time(() => renderFileStrip(container, model ?? buildModel(data)) as ViewHandle<BaseOptions>);
    log(`renderFileStrip: ${elapsed.toFixed(1)} ms, ${handle!.element.querySelectorAll("circle.pqax-dot").length} dots`);
    expect(handle!.element.querySelectorAll("g.pqax-strip-row")).toHaveLength(40);
    expect(elapsed).toBeLessThan(BOUND_MS);
  });
});
