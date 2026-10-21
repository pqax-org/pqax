// The tests live in tests/typescript-views at the root of the repository. They import the package as "@pqax/views" and
// the reader as "@pqax/pqax", both mapped to their TypeScript sources, so the tests need no build. The files of the
// conformance kit come from the module "virtual:pqax-kit" of the configuration of the reader, and the helpers of
// tests/typescript/kit.ts read them. In Node, the tests that render, *.browser.test.ts, are left out:
// vitest.browser.config.ts runs every test in the browsers.
import { fileURLToPath } from "node:url";
import { configDefaults, defineConfig, mergeConfig } from "vitest/config";
import reader from "../pqax/vitest.config.ts";

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));

export default mergeConfig(reader, defineConfig({
  resolve: {
    alias: [
      { find: /^@pqax\/views$/, replacement: here("./src/index.ts") },
      { find: /^@pqax\/views\/styles\.css$/, replacement: here("./styles.css") },
    ],
  },
  test: {
    dir: here("../../../tests/typescript-views"),
    include: ["**/*.test.ts"],
    exclude: [...configDefaults.exclude, "**/*.browser.test.ts"],
  },
}));
