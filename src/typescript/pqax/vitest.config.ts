// The tests live in tests/typescript at the root of the repository, outside this package. They import the package by
// its name, "pqax" and "@pqax/pqax/node", which resolves to the TypeScript sources, so the tests need no build.
//
// The tests read the files of the conformance kit from the module "virtual:pqax-kit", which this configuration makes
// from conformance/ when the tests start. The same tests then run in Node and in browsers, which cannot read files.
// vitest.browser.config.ts runs them in browsers.
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

const here = (path: string): string => fileURLToPath(new URL(path, import.meta.url));

const KIT = here("../../../conformance");
const KIT_MODULE = "virtual:pqax-kit";

/** The files of the kit that the tests read, by their path in conformance/, with forward slashes. */
function kitFiles(): string[] {
  const files = ["expected", "samples"].flatMap((folder) =>
    readdirSync(join(KIT, folder), { recursive: true, withFileTypes: true })
      .filter((entry) => entry.isFile())
      .map((entry) => join(entry.parentPath, entry.name).slice(KIT.length + 1).replaceAll("\\", "/")));
  return ["rules.md", ...files].sort();
}

export default defineConfig({
  plugins: [
    {
      // The module exports root, the folder of the kit on disk, and files, the content of each file in base64.
      name: "pqax-kit",
      resolveId: (id) => (id === KIT_MODULE ? `\0${KIT_MODULE}` : undefined),
      load(id) {
        if (id !== `\0${KIT_MODULE}`) {
          return undefined;
        }
        const files: Record<string, string> = {};
        for (const path of kitFiles()) {
          this.addWatchFile(join(KIT, path));
          files[path] = readFileSync(join(KIT, path)).toString("base64");
        }
        return `export const root = ${JSON.stringify(KIT)};\nexport const files = ${JSON.stringify(files)};\n`;
      },
    },
  ],
  resolve: {
    alias: [
      { find: /^@pqax\/pqax$/, replacement: here("./src/index.ts") },
      { find: /^@pqax\/pqax\/node$/, replacement: here("./src/node.ts") },
    ],
  },
  test: {
    dir: here("../../../tests/typescript"),
    include: ["**/*.test.ts"],
  },
});
