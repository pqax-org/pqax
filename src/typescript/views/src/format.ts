// How the views write numbers: bytes in 1024-based units, counts with the separators of the locale, shares as
// percentages, and times as dates. A value that is null is written as "n/a".

/** The text of a missing value. */
export const MISSING = "n/a";

const UNITS = ["B", "KB", "MB", "GB", "TB", "PB"];

/**
 * Bytes as text, in 1024-based units: `512 B`, `1.5 KB`, `21.5 GB`.
 * @param value The bytes.
 * @param locale The locale of the separators. Default the locale of the browser.
 */
export function formatBytes(value: number | null | undefined, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return MISSING;
  }
  let scaled = Math.abs(value);
  let unit = 0;
  while (scaled >= 1024 && unit < UNITS.length - 1) {
    scaled /= 1024;
    unit++;
  }
  const digits = unit === 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
  const text = scaled.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
  return `${value < 0 ? "-" : ""}${text} ${UNITS[unit]}`;
}

/**
 * A count as text, with the separators of the locale.
 * @param value The count.
 * @param locale The locale. Default the locale of the browser.
 */
export function formatCount(value: number | null | undefined, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return MISSING;
  }
  return Math.round(value).toLocaleString(locale);
}

/**
 * A share from 0 to 1 as a percentage: `36.7%`. A share that is not 0 but rounds to it is written `<0.1%`.
 * @param value The share.
 * @param locale The locale. Default the locale of the browser.
 */
export function formatShare(value: number | null | undefined, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return MISSING;
  }
  if (value > 0 && value < 0.0005) {
    return `<${(0.1).toLocaleString(locale)}%`;
  }
  return `${(value * 100).toLocaleString(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}%`;
}

/**
 * A ratio or a rate with two significant decimals: `2.01`, `0.25`.
 * @param value The number.
 * @param locale The locale. Default the locale of the browser.
 */
export function formatNumber(value: number | null | undefined, locale?: string): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return MISSING;
  }
  const digits = Math.abs(value) >= 100 ? 0 : Math.abs(value) >= 10 ? 1 : 2;
  return value.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

/**
 * A timestamp of the format as a date and a time in UTC: `2026-10-05 08:15 UTC`. The text as it is when it does not parse.
 * @param value The timestamp, such as `2026-10-05T08:15:30.000Z`.
 */
export function formatTime(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return MISSING;
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) {
    return value;
  }
  return `${new Date(parsed).toISOString().slice(0, 16).replace("T", " ")} UTC`;
}
