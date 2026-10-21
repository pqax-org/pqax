// The squarified treemap layout of Bruls, Huizing, and van Wijk (2000). The items, largest first, fill the rectangle
// in rows. Each row lies along the shorter side of the space that is left, and takes items as long as its worst aspect
// ratio does not grow. The areas are proportional to the weights and sum to the area of the rectangle. The layout runs
// in linear time after the sort, so that thousands of items take a few milliseconds.

/** A rectangle: its top left corner, its width, and its height. */
export interface LayoutRect {
  /** The left edge. */
  readonly x: number;
  /** The top edge. */
  readonly y: number;
  /** The width, 0 or more. */
  readonly width: number;
  /** The height, 0 or more. */
  readonly height: number;
}

/** An item to lay out: its area is proportional to its weight. */
export interface WeightedItem {
  /** The weight. An item of weight 0 or less, or not finite, gets no area. */
  readonly weight: number;
}

/** Whether a weight takes area: finite and above 0. */
function counts(weight: number): boolean {
  return typeof weight === "number" && Number.isFinite(weight) && weight > 0;
}

/** The worst aspect ratio of a row of a given sum along a side, from its largest and its smallest area. */
function worstRatio(largest: number, smallest: number, sum: number, side: number): number {
  const sum2 = sum * sum;
  const side2 = side * side;
  return Math.max((side2 * largest) / sum2, sum2 / (side2 * smallest));
}

/**
 * Lays out items in a rectangle with areas proportional to their weights. Returns one entry per item, in the order of
 * the input; an item with a weight of 0 or less, or not finite, gets a rect of width and height 0 at the origin of the
 * rectangle. Every rect lies inside the rectangle, and the areas sum to its area.
 * @param items The items. The function sorts a copy, largest first; ties keep the order of the input.
 * @param rect The rectangle to fill.
 */
export function squarify<T extends WeightedItem>(items: readonly T[], rect: LayoutRect): { item: T; rect: LayoutRect }[] {
  const out = items.map((item) => ({ item, rect: { x: rect.x, y: rect.y, width: 0, height: 0 } as LayoutRect }));
  const width = Number.isFinite(rect.width) ? Math.max(0, rect.width) : 0;
  const height = Number.isFinite(rect.height) ? Math.max(0, rect.height) : 0;
  const order: number[] = [];
  let largest = 0;
  for (let i = 0; i < items.length; i++) {
    const weight = items[i]!.weight;
    if (counts(weight)) {
      order.push(i);
      largest = Math.max(largest, weight);
    }
  }
  if (order.length === 0 || width <= 0 || height <= 0) {
    return out;
  }
  order.sort((a, b) => items[b]!.weight - items[a]!.weight || a - b);
  // The weights over the largest, so that the sum cannot overflow.
  let total = 0;
  for (const i of order) {
    total += items[i]!.weight / largest;
  }
  const scale = (width * height) / total;
  const areas = order.map((i) => (items[i]!.weight / largest) * scale);

  let x = rect.x;
  let y = rect.y;
  let w = width;
  let h = height;
  let start = 0;
  while (start < order.length) {
    const side = Math.min(w, h);
    let sum = areas[start]!;
    let worst = worstRatio(sum, sum, sum, side);
    let end = start + 1;
    while (end < order.length) {
      const next = sum + areas[end]!;
      const ratio = worstRatio(areas[start]!, areas[end]!, next, side);
      if (ratio > worst) {
        break;
      }
      sum = next;
      worst = ratio;
      end++;
    }
    const last = end === order.length;
    const vertical = w >= h;
    // The row is a column at the left when the space is wide, a line at the top when it is tall. The last row takes
    // what is left, so that floating error does not leave a sliver.
    const length = vertical ? h : w;
    const room = vertical ? w : h;
    const thickness = last ? room : Math.min(room, length > 0 ? sum / length : 0);
    let offset = 0;
    for (let k = start; k < end; k++) {
      const size = k === end - 1 ? Math.max(0, length - offset) : Math.min(Math.max(0, length - offset), thickness > 0 ? areas[k]! / thickness : 0);
      out[order[k]!]!.rect = vertical
        ? { x, y: y + offset, width: thickness, height: size }
        : { x: x + offset, y, width: size, height: thickness };
      offset += size;
    }
    if (vertical) {
      x += thickness;
      w = Math.max(0, w - thickness);
    } else {
      y += thickness;
      h = Math.max(0, h - thickness);
    }
    start = end;
  }
  return out;
}
