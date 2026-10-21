// Serves a folder over HTTP on 127.0.0.1, to try the assembled site in a browser, which does not run ES modules from
// files on disk. It also serves the samples of the conformance kit under /_samples/, for the checks of the page.
//
//   node scripts/serve.mjs [folder] [port]    default: .artifacts/site at the root of the repository, port 8080
import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import { createServer } from "node:http";
import { extname, join, normalize, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const folder = resolve(process.argv[2] ?? join(root, ".artifacts", "site"));
const port = Number(process.argv[3] ?? 8080);
const samples = join(root, "conformance", "samples");

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8",
};

/** The file for a path of a URL under a base folder, or null when it leaves the folder. */
function within(base, path) {
  const file = normalize(join(base, decodeURIComponent(path)));
  return file === base || file.startsWith(base + sep) ? file : null;
}

createServer(async (request, response) => {
  const path = new URL(request.url ?? "/", "http://localhost").pathname;
  let file = path.startsWith("/_samples/") ? within(samples, path.slice("/_samples".length)) : within(folder, path);
  try {
    if (file !== null && (await stat(file)).isDirectory()) {
      file = join(file, "index.html");
    }
    if (file === null || !(await stat(file)).isFile()) {
      throw new Error("not found");
    }
  } catch {
    response.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
    response.end("Not found\n");
    return;
  }
  response.writeHead(200, { "content-type": TYPES[extname(file).toLowerCase()] ?? "application/octet-stream", "cache-control": "no-store" });
  createReadStream(file).pipe(response);
}).listen(port, "127.0.0.1", () => console.log(`Serving ${folder} at http://127.0.0.1:${port}/`));
