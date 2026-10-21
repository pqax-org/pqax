// The layout of the treemap, which needs no DOM: squarify, with the areas, the bounds, the order, the overlaps, the
// aspect ratios, the edge cases of the weights and of the rectangle, and its time on thousands of items; and the bins
// of the share of nulls that color the tiles. It runs in Node and in the browsers.
import { describe, expect, it } from "vitest";
import { type LayoutRect, squarify } from "@pqax/views";
import { nullShareBin } from "@pqax/views";
import { generator } from "./synthetic";

const area = (r: LayoutRect) => r.width * r.height;
const RECT: LayoutRect = { x: 10, y: 20, width: 800, height: 420 };

/** Weights from a seeded generator, spread over four orders of magnitude, as the columns of a footprint are. */
function weights(count: number, seed = 1): { weight: number }[] {
  const random = generator(seed);
  return Array.from({ length: count }, () => ({ weight: Math.round(10 ** (random() * 4)) }));
}

function expectInside(r: LayoutRect, outer: LayoutRect): void {
  const eps = 1e-6;
  expect(r.width).toBeGreaterThanOrEqual(0);
  expect(r.height).toBeGreaterThanOrEqual(0);
  expect(r.x).toBeGreaterThanOrEqual(outer.x - eps);
  expect(r.y).toBeGreaterThanOrEqual(outer.y - eps);
  expect(r.x + r.width).toBeLessThanOrEqual(outer.x + outer.width + eps);
  expect(r.y + r.height).toBeLessThanOrEqual(outer.y + outer.height + eps);
}

/** The area that two rects share. */
function overlap(a: LayoutRect, b: LayoutRect): number {
  const w = Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x);
  const h = Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y);
  return w > 0 && h > 0 ? w * h : 0;
}

function expectProportional(items: readonly { weight: number }[], rects: readonly LayoutRect[], outer: LayoutRect): void {
  const total = items.reduce((sum, item) => sum + (Number.isFinite(item.weight) && item.weight > 0 ? item.weight : 0), 0);
  const sum = rects.reduce((s, r) => s + area(r), 0);
  expect(Math.abs(sum - area(outer)) / area(outer)).toBeLessThan(1e-6);
  items.forEach((item, i) => {
    if (Number.isFinite(item.weight) && item.weight > 0) {
      const expected = (item.weight / total) * area(outer);
      expect(Math.abs(area(rects[i]!) - expected) / expected).toBeLessThan(1e-6);
    }
  });
}

describe("squarify", () => {
  it("returns nothing for no item", () => {
    expect(squarify([], RECT)).toEqual([]);
  });

  it("gives the whole rectangle to one item", () => {
    const item = { weight: 5 };
    const [only] = squarify([item], RECT);
    expect(only!.item).toBe(item);
    expect(only!.rect.x).toBeCloseTo(RECT.x, 9);
    expect(only!.rect.y).toBeCloseTo(RECT.y, 9);
    expect(only!.rect.width).toBeCloseTo(RECT.width, 9);
    expect(only!.rect.height).toBeCloseTo(RECT.height, 9);
  });

  it("returns one entry per item, in the order of the input", () => {
    const items = weights(50).map((w, i) => ({ ...w, name: `col_${i}` }));
    const out = squarify(items, RECT);
    expect(out).toHaveLength(items.length);
    out.forEach((entry, i) => expect(entry.item).toBe(items[i]));
  });

  it("gives a zero rect to a weight of zero, below zero, or not finite", () => {
    const items = [{ weight: 3 }, { weight: 0 }, { weight: -2 }, { weight: Number.NaN }, { weight: 1 }, { weight: Infinity }, { weight: -Infinity }];
    const out = squarify(items, RECT);
    expect(out).toHaveLength(items.length);
    for (const i of [1, 2, 3, 5, 6]) {
      expect(out[i]!.rect.width * out[i]!.rect.height).toBe(0);
      expect(out[i]!.rect.width).toBe(0);
      expect(out[i]!.rect.height).toBe(0);
    }
    expectProportional(items, out.map((o) => o.rect), RECT);
    expect(area(out[0]!.rect) / area(out[4]!.rect)).toBeCloseTo(3, 6);
  });

  it("gives zero rects when every weight is zero", () => {
    const out = squarify([{ weight: 0 }, { weight: 0 }], RECT);
    expect(out.map((o) => area(o.rect))).toEqual([0, 0]);
  });

  it("gives zero rects in a rectangle of no width or of no height", () => {
    for (const outer of [{ ...RECT, width: 0 }, { ...RECT, height: 0 }, { ...RECT, width: -5 }, { ...RECT, width: Number.NaN }]) {
      const out = squarify(weights(10), outer);
      expect(out).toHaveLength(10);
      expect(out.every((o) => o.rect.width === 0 && o.rect.height === 0)).toBe(true);
    }
  });

  it.each([2, 3, 7, 40, 1000])("lays out %i items inside the rectangle, with areas proportional to the weights", (count) => {
    const items = weights(count, count);
    const rects = squarify(items, RECT).map((o) => o.rect);
    for (const r of rects) {
      expectInside(r, RECT);
    }
    expectProportional(items, rects, RECT);
  });

  it("lays out items of very different weights, from 1 to 10^12", () => {
    const items = [{ weight: 1e12 }, { weight: 1 }, { weight: 1e6 }, { weight: 3 }];
    const rects = squarify(items, RECT).map((o) => o.rect);
    rects.forEach((r) => expectInside(r, RECT));
    expect(rects.reduce((s, r) => s + area(r), 0)).toBeCloseTo(area(RECT), 3);
    // An item of 10^-12 of the total gets a fraction of a pixel: its error is measured against the whole area.
    const total = items.reduce((sum, item) => sum + item.weight, 0);
    items.forEach((item, i) => {
      expect(Math.abs(area(rects[i]!) - (item.weight / total) * area(RECT))).toBeLessThan(area(RECT) * 1e-12);
    });
  });

  it("does not overlap two rects of positive area, on a sample of the pairs of 1,000 items", () => {
    const items = weights(1000, 9);
    const rects = squarify(items, RECT).map((o) => o.rect);
    const random = generator(3);
    const tolerance = area(RECT) * 1e-9;
    let checked = 0;
    // Every pair of the 60 largest, and 20,000 pairs drawn at random.
    const order = [...rects.keys()].sort((a, b) => items[b]!.weight - items[a]!.weight).slice(0, 60);
    for (let i = 0; i < order.length; i++) {
      for (let j = i + 1; j < order.length; j++) {
        expect(overlap(rects[order[i]!]!, rects[order[j]!]!)).toBeLessThanOrEqual(tolerance);
        checked++;
      }
    }
    for (let k = 0; k < 20000; k++) {
      const i = Math.floor(random() * rects.length);
      const j = Math.floor(random() * rects.length);
      if (i !== j && area(rects[i]!) > 0 && area(rects[j]!) > 0) {
        expect(overlap(rects[i]!, rects[j]!)).toBeLessThanOrEqual(tolerance);
        checked++;
      }
    }
    expect(checked).toBeGreaterThan(20000);
  });

  it("keeps the aspect ratios near 1 for equal weights", () => {
    for (const [count, outer] of [[16, { x: 0, y: 0, width: 400, height: 400 }], [100, RECT], [7, { x: 0, y: 0, width: 300, height: 900 }]] as const) {
      const rects = squarify(Array.from({ length: count }, () => ({ weight: 1 })), outer).map((o) => o.rect);
      const worst = Math.max(...rects.map((r) => Math.max(r.width / r.height, r.height / r.width)));
      expect(worst).toBeLessThan(3);
    }
    const square = squarify(Array.from({ length: 16 }, () => ({ weight: 1 })), { x: 0, y: 0, width: 400, height: 400 }).map((o) => o.rect);
    expect(Math.max(...square.map((r) => Math.max(r.width / r.height, r.height / r.width)))).toBeLessThan(1.5);
  });

  it("lays out 1,000 and 5,000 items in little time", () => {
    for (const count of [1000, 5000]) {
      const items = weights(count, 5);
      const start = performance.now();
      const out = squarify(items, RECT);
      const elapsed = performance.now() - start;
      console.log(`squarify of ${count} items: ${elapsed.toFixed(1)} ms`);
      expect(out).toHaveLength(count);
      expect(elapsed).toBeLessThan(500);
      expectProportional(items, out.map((o) => o.rect), RECT);
    }
  });
});

describe("nullShareBin", () => {
  it("puts each share in its bin, with the bounds in the lower bin", () => {
    expect(nullShareBin(null)).toBe("none");
    expect(nullShareBin(Number.NaN)).toBe("none");
    expect(nullShareBin(0)).toBe(0);
    expect(nullShareBin(0.0001)).toBe(1);
    expect(nullShareBin(0.05)).toBe(1);
    expect(nullShareBin(0.0501)).toBe(2);
    expect(nullShareBin(0.2)).toBe(2);
    expect(nullShareBin(0.5)).toBe(3);
    expect(nullShareBin(0.8)).toBe(4);
    expect(nullShareBin(0.81)).toBe(5);
    expect(nullShareBin(1)).toBe(5);
  });
});
