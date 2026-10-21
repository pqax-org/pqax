// The tests of vitest.config.ts in Chromium, Firefox, and WebKit, the engine of Safari, through Playwright, with the
// tests that render, *.browser.test.ts. The browsers come from `npx playwright install chromium firefox webkit`.
import { fileURLToPath } from "node:url";
import { playwright } from "@vitest/browser-playwright";
import { configDefaults, defineConfig, mergeConfig } from "vitest/config";
import base from "./vitest.config.ts";

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));

const config = mergeConfig(base, defineConfig({
  // The browsers load the tests from the server of vitest, which serves only the folders listed here.
  server: {
    fs: {
      allow: [here("."), here("../pqax"), here("../../../tests/typescript-views"), here("../../../tests/typescript")],
    },
  },
  test: {
    browser: {
      enabled: true,
      provider: playwright(),
      headless: true,
      screenshotFailures: false,
      instances: [{ browser: "chromium" }, { browser: "firefox" }, { browser: "webkit" }],
    },
  },
}));

// mergeConfig joins the arrays, so the exclusion of the browser tests in vitest.config.ts is replaced here.
config.test!.exclude = [...configDefaults.exclude];
export default config;
