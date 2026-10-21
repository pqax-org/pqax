// Assembles the published folder of pqax.org: site/ as it is, plus the built packages under tools/lib/, where the
// import map of site/tools/index.html finds them. Run it after `npm run build` in src/typescript/pqax and here.
//
//   node scripts/site.mjs [output]    default: .artifacts/site at the root of the repository
//
// The output folder is emptied first. Only the JavaScript of the packages, their source maps, and the stylesheet are
// copied: no declaration file, and not the Node entry of the reader.
import { cp, mkdir, readdir, rm, stat } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const output = resolve(process.argv[2] ?? join(root, ".artifacts", "site"));
const reader = join(root, "src", "typescript", "pqax");
const views = join(root, "src", "typescript", "views");

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

/** Copies the .js and .js.map files of a dist folder, but the excluded names. */
async function copyDist(from, to, exclude = []) {
  if (!(await exists(join(from, "index.js")))) {
    throw new Error(`${from} has no index.js: run npm run build in its package first`);
  }
  await mkdir(to, { recursive: true });
  let count = 0;
  for (const name of await readdir(from)) {
    const base = name.replace(/\.js(\.map)?$/, "");
    if (!/\.js(\.map)?$/.test(name) || exclude.includes(base)) {
      continue;
    }
    await cp(join(from, name), join(to, name));
    count++;
  }
  return count;
}

await rm(output, { recursive: true, force: true });
await cp(join(root, "site"), output, { recursive: true });
const lib = join(output, "tools", "lib");
const readerFiles = await copyDist(join(reader, "dist"), join(lib, "pqax"), ["node"]);
const viewFiles = await copyDist(join(views, "dist"), join(lib, "views"));
await cp(join(views, "styles.css"), join(lib, "views", "styles.css"));
console.log(`${output}: site/, ${readerFiles} files of @pqax/pqax, ${viewFiles + 1} files of @pqax/views`);
