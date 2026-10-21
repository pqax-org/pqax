// What the three views share: the handle and the common options, the root element with its theme, the creation of
// HTML and SVG elements, the tooltip with the figures of an item, keyboard focus, and the observer of the width. No
// color is set here: every color comes from the custom properties of styles.css.
import type { StorageModel } from "./model.js";

/** The color scheme of a view: the one of the page (`auto`), or forced. */
export type ViewTheme = "auto" | "light" | "dark";

/** The options that the three views share. */
export interface BaseOptions {
  /** `auto` follows `prefers-color-scheme` and a `data-theme` attribute on an ancestor; `light` and `dark` force one. Default `auto`. */
  readonly theme?: ViewTheme;
  /** The locale of the numbers. Default the locale of the browser. */
  readonly locale?: string;
  /** The accessible name of the view. */
  readonly label?: string;
  /** Called with the key of an item when the user clicks it or presses Enter on it. */
  readonly onSelect?: (key: string) => void;
}

/** What a render function returns. */
export interface ViewHandle<O> {
  /** The root element of the view, inside the container. */
  readonly element: HTMLElement;
  /** Draws the view again, with a new model, new options, or both. Options merge with the current ones. */
  update(model?: StorageModel, options?: Partial<O>): void;
  /** Removes the view and its listeners from the container. */
  dispose(): void;
}

const SVG_NS = "http://www.w3.org/2000/svg";

type Attributes = Record<string, string | number | boolean | null | undefined>;

function setAttributes(element: Element, attributes: Attributes): void {
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined || value === false) {
      continue;
    }
    element.setAttribute(name, value === true ? "" : String(value));
  }
}

/** Creates an HTML element with attributes and children. Strings become text nodes. */
export function html<K extends keyof HTMLElementTagNameMap>(tag: K, attributes: Attributes = {}, ...children: (Node | string | null | undefined)[]): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag);
  setAttributes(element, attributes);
  for (const child of children) {
    if (child !== null && child !== undefined) {
      element.append(child);
    }
  }
  return element;
}

/** Creates an SVG element with attributes and children. */
export function svg<K extends keyof SVGElementTagNameMap>(tag: K, attributes: Attributes = {}, ...children: (Node | string | null | undefined)[]): SVGElementTagNameMap[K] {
  const element = document.createElementNS(SVG_NS, tag);
  setAttributes(element, attributes);
  for (const child of children) {
    if (child !== null && child !== undefined) {
      element.append(child);
    }
  }
  return element;
}

/**
 * Creates the root of a view inside the container: a `div` with the classes `pqax-view` and `pqax-<kind>`, and
 * `data-theme` when the theme is forced.
 */
export function createRoot(container: HTMLElement, kind: string, options: BaseOptions): HTMLElement {
  const root = html("div", { class: `pqax-view pqax-${kind}`, role: "group" });
  applyBaseOptions(root, options);
  container.append(root);
  return root;
}

/** Sets the theme and the accessible name of a root from the options. */
export function applyBaseOptions(root: HTMLElement, options: BaseOptions): void {
  if (options.theme === "light" || options.theme === "dark") {
    root.setAttribute("data-theme", options.theme);
  } else {
    root.removeAttribute("data-theme");
  }
  if (options.label !== undefined) {
    root.setAttribute("aria-label", options.label);
  }
}

/** The figures of the items, shown in the tooltip and given as the accessible name. */
const details = new WeakMap<Element, readonly string[]>();

/**
 * Makes an element an item of a view: the class `pqax-item`, the key in `data-key`, keyboard focus, and the figures
 * that the tooltip shows. The first line is the title; the accessible name joins the lines.
 */
export function makeItem(element: Element, key: string, lines: readonly string[], role = "button"): void {
  element.classList.add("pqax-item");
  element.setAttribute("data-key", key);
  element.setAttribute("tabindex", "0");
  element.setAttribute("role", role);
  element.setAttribute("aria-label", lines.join(". "));
  details.set(element, lines);
}

/** The figures of an item, as given to {@link makeItem}. */
export function itemDetail(element: Element): readonly string[] | undefined {
  return details.get(element);
}

/**
 * The tooltip of a view: one element, shown next to the item under the pointer or with the focus. It returns a
 * function that removes its listeners.
 */
export function attachTooltip(root: HTMLElement): () => void {
  const tip = html("div", { class: "pqax-tooltip", role: "tooltip", hidden: true });
  root.append(tip);
  let current: Element | null = null;

  const itemOf = (target: EventTarget | null): Element | null =>
    target instanceof Element ? target.closest(".pqax-item") : null;

  const show = (item: Element, clientX?: number, clientY?: number) => {
    const lines = details.get(item);
    if (lines === undefined || lines.length === 0) {
      hide();
      return;
    }
    if (current !== item) {
      tip.replaceChildren(html("strong", {}, lines[0]!), ...lines.slice(1).map((line) => html("span", {}, line)));
      current = item;
    }
    tip.hidden = false;
    const box = root.getBoundingClientRect();
    const target = item.getBoundingClientRect();
    const x = clientX ?? target.left + target.width / 2;
    const y = clientY ?? target.bottom;
    const width = tip.offsetWidth;
    const height = tip.offsetHeight;
    let left = x - box.left + 12;
    if (left + width > box.width) {
      left = Math.max(0, x - box.left - width - 12);
    }
    let top = y - box.top + 12;
    if (clientY === undefined) {
      top = target.bottom - box.top + 4;
    }
    if (top + height > box.height && y - box.top - height - 12 >= 0) {
      top = (clientY === undefined ? target.top : y) - box.top - height - 8;
    }
    tip.style.left = `${Math.round(left)}px`;
    tip.style.top = `${Math.round(top)}px`;
  };
  const hide = () => {
    tip.hidden = true;
    current = null;
  };

  const onMove = (event: PointerEvent) => {
    const item = itemOf(event.target);
    if (item === null) {
      hide();
    } else {
      show(item, event.clientX, event.clientY);
    }
  };
  const onLeave = () => hide();
  const onFocus = (event: FocusEvent) => {
    const item = itemOf(event.target);
    if (item !== null) {
      show(item);
    }
  };
  const onBlur = () => hide();
  const onKey = (event: KeyboardEvent) => {
    if (event.key === "Escape") {
      hide();
    }
  };
  root.addEventListener("pointermove", onMove);
  root.addEventListener("pointerleave", onLeave);
  root.addEventListener("focusin", onFocus);
  root.addEventListener("focusout", onBlur);
  root.addEventListener("keydown", onKey);
  return () => {
    root.removeEventListener("pointermove", onMove);
    root.removeEventListener("pointerleave", onLeave);
    root.removeEventListener("focusin", onFocus);
    root.removeEventListener("focusout", onBlur);
    root.removeEventListener("keydown", onKey);
    tip.remove();
  };
}

/**
 * Keyboard support for the items of a root: the arrow keys, Home, and End move the focus between the items in the
 * order of the document, and Enter or Space activates the item with a click. It returns a function that removes the
 * listener.
 */
export function attachKeyboard(root: HTMLElement): () => void {
  const onKey = (event: KeyboardEvent) => {
    const target = event.target instanceof Element ? event.target : null;
    if (target === null || !target.classList.contains("pqax-item")) {
      return;
    }
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      target.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
      return;
    }
    const items = [...root.querySelectorAll<HTMLElement | SVGElement>(".pqax-item")];
    const index = items.indexOf(target as HTMLElement | SVGElement);
    let next = -1;
    if (event.key === "ArrowDown" || event.key === "ArrowRight") {
      next = Math.min(items.length - 1, index + 1);
    } else if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
      next = Math.max(0, index - 1);
    } else if (event.key === "Home") {
      next = 0;
    } else if (event.key === "End") {
      next = items.length - 1;
    }
    if (next >= 0 && next !== index) {
      event.preventDefault();
      items[next]!.focus();
    }
  };
  root.addEventListener("keydown", onKey);
  return () => root.removeEventListener("keydown", onKey);
}

/**
 * Calls back when the width of an element changes by a pixel or more. It returns a function that stops observing.
 * Without `ResizeObserver` it does nothing.
 */
export function observeWidth(element: HTMLElement, callback: (width: number) => void): () => void {
  if (typeof ResizeObserver === "undefined") {
    return () => undefined;
  }
  let last = Math.round(element.clientWidth);
  let frame = 0;
  const observer = new ResizeObserver(() => {
    const width = Math.round(element.clientWidth);
    if (width !== last) {
      last = width;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => callback(width));
    }
  });
  observer.observe(element);
  return () => {
    cancelAnimationFrame(frame);
    observer.disconnect();
  };
}

/** Below this width in pixels, a view takes its phone layout: the stack puts the label above the bar, and the treemap is as tall as it is wide. */
export const NARROW_WIDTH = 480;

/** The width available to a view: the width of its root, else of the container, else 640. */
export function widthOf(root: HTMLElement, container: HTMLElement): number {
  return Math.round(root.clientWidth || container.clientWidth || 640);
}
