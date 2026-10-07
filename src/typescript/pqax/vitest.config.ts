// The tests live in tests/typescript at the root of the repository, outside this package. They import the package by
// its name, "pqax" and "pqax/node", which resolves to the TypeScript sources, so the tests need no build.
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));

export default defineConfig({
  resolve: {
    alias: [
      { find: /^pqax$/, replacement: here("./src/index.ts") },
      { find: /^pqax\/node$/, replacement: here("./src/node.ts") },
    ],
  },
  test: {
    dir: here("../../../tests/typescript"),
    include: ["**/*.test.ts"],
  },
});
