// The GitHub the throwaway host binds to. Every repo exists and holds the fixture pipeline, named after
// the repo, so a test gets a unique pipeline from a unique repo name. POST /_test/repos/<owner>/<name>/commit
// with {"files": {"<path>": "<content>"}} lands a commit on top of it.
import { cpSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

const FIXTURE_DIR = process.env.FIXTURE_DIR!;
const SDK_DIR = process.env.SDK_DIR!;

type Repo = { commits: { sha: string; date: string }[]; files: Record<string, string> };
const repos = new Map<string, Repo>();

const hash = (text: string) => new Bun.CryptoHasher("sha1").update(text).digest("hex");

function repo(name: string): Repo {
  let found = repos.get(name);
  if (!found) {
    found = { commits: [{ sha: hash(name), date: new Date().toISOString() }], files: {} };
    repos.set(name, found);
  }
  return found;
}

const head = (r: Repo) => r.commits[r.commits.length - 1]!;
const pipelineName = (name: string) => name.split("/")[1]!;

async function fixtureFile(name: string, path: string): Promise<string | null> {
  const r = repo(name);
  if (path in r.files) return r.files[path]!;
  const file = Bun.file(join(FIXTURE_DIR, path));
  if (!(await file.exists())) return null;
  return (await file.text()).replaceAll("__PIPELINE_NAME__", pipelineName(name));
}

async function tarball(name: string, sha: string): Promise<Response> {
  const r = repo(name);
  if (!r.commits.some((c) => c.sha === sha)) return new Response("no such commit", { status: 404 });

  const work = mkdtempSync("/tmp/tarball-");
  const root = join(work, `${name.replace("/", "-")}-${sha.slice(0, 7)}`);
  try {
    cpSync(FIXTURE_DIR, root, { recursive: true });
    cpSync(SDK_DIR, join(root, "src/sdk"), {
      recursive: true,
      filter: (source) => !source.includes("/node_modules") && !source.includes("/.tmp"),
    });
    const pipeline = join(root, ".pipelines/pipeline.ts");
    writeFileSync(pipeline, (await Bun.file(pipeline).text()).replaceAll("__PIPELINE_NAME__", pipelineName(name)));
    for (const [path, content] of Object.entries(r.files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), content);
    }
    const tar = Bun.spawn(["tar", "-czf", "-", "-C", work, root.slice(work.length + 1)], { stdout: "pipe" });
    const bytes = await new Response(tar.stdout).arrayBuffer();
    if ((await tar.exited) !== 0) return new Response("tar failed", { status: 500 });
    return new Response(bytes, { headers: { "content-type": "application/gzip" } });
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

Bun.serve({
  port: 8080,
  idleTimeout: 60,
  async fetch(request) {
    const url = new URL(request.url);
    const path = decodeURIComponent(url.pathname);

    let m = /^\/_test\/repos\/([^/]+\/[^/]+)\/commit$/.exec(path);
    if (m && request.method === "POST") {
      const r = repo(m[1]!);
      const { files } = (await request.json()) as { files: Record<string, string> };
      Object.assign(r.files, files);
      const commit = { sha: hash(`${m[1]}#${r.commits.length}`), date: new Date().toISOString() };
      r.commits.push(commit);
      return Response.json({ sha: commit.sha });
    }

    m = /^\/codeload\/([^/]+\/[^/]+)\/tar\.gz\/([0-9a-f]{40})$/.exec(path);
    if (m) return tarball(m[1]!, m[2]!);

    m = /^\/repos\/([^/]+\/[^/]+)\/commits$/.exec(path);
    if (m) {
      const commit = head(repo(m[1]!));
      const etag = `"${commit.sha}"`;
      if (request.headers.get("if-none-match") === etag) return new Response(null, { status: 304 });
      return Response.json([{ sha: commit.sha, commit: { committer: { date: commit.date } } }], {
        headers: { etag },
      });
    }

    m = /^\/repos\/([^/]+\/[^/]+)\/commits\/(.+)$/.exec(path);
    if (m) {
      const commit = head(repo(m[1]!));
      return Response.json({ sha: commit.sha, commit: { committer: { date: commit.date } } });
    }

    m = /^\/repos\/([^/]+\/[^/]+)\/contents\/(.+)$/.exec(path);
    if (m) {
      const content = await fixtureFile(m[1]!, m[2]!);
      return content === null ? new Response("not found", { status: 404 }) : new Response(content);
    }

    return new Response("not found", { status: 404 });
  },
});
