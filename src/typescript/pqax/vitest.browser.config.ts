// The tests of vitest.config.ts in Chromium, Firefox, and WebKit, the engine of Safari, through Playwright: every test
// file but node.test.ts, which tests @pqax/pqax/node and needs Node. The browsers come from `npx playwright install chromium
// firefox webkit`.
import { fileURLToPath } from "node:url";
import { playwright } from "@vitest/browser-playwright";
import { configDefaults, defineConfig, mergeConfig } from "vitest/config";
import base from "./vitest.config.ts";

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));

export default mergeConfig(base, defineConfig({
  // The browsers load the tests from the server of vitest, which serves only the folders listed here.
  server: { fs: { allow: [here("."), here("../../../tests/typescript")] } },
  test: {
    exclude: [...configDefaults.exclude, "**/node.test.ts"],
    browser: {
      enabled: true,
      provider: playwright(),
      headless: true,
      screenshotFailures: false,
      instances: [{ browser: "chromium" }, { browser: "firefox" }, { browser: "webkit" }],
    },
  },
}));
