// steps.gitHubSource — a GitHub repository at a commit, as a `Source`.
//
// Runs entirely inside the harness (HTTP + tar extraction in Bun): no git, wget or tar is needed
// in the image, so v1's busybox-wget TLS footgun (`--no-check-certificate`) is gone.
import { mkdirSync, mkdtempSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import type { Ctx } from "../ctx/types.ts";
import type { Secret } from "../core/secrets.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import { extractTar } from "../util/tar.ts";
import { Source } from "./models.ts";

/** Image the step runs in. The work happens in the harness, so any image would do. */
export const GITHUB_SOURCE_IMAGE = "alpine:3.22";

const input = { repo: z.string().regex(/^[\w.-]+\/[\w.-]+$/, "expected owner/name"), sha: z.string().min(1) };
export type GitHubSourceInput = typeof input;

export interface GitHubSourceDef<N extends string, T extends string> {
  name: LiteralName<N>;
  /** Token sent to the GitHub API (needed for private repositories; raises the rate limit). */
  token?: Secret<T>;
  timeoutSeconds?: number;
}

/** Tarball URL of `repo` at `sha` (the API redirects to codeload.github.com). */
export const gitHubTarballUrl = (repo: string, sha: string): string => `https://api.github.com/repos/${repo}/tarball/${encodeURIComponent(sha)}`;

/**
 * Downloads `repo` at `sha` from GitHub as a tarball and outputs it as a `Source` whose `tree` is the
 * repository root (the tarball's single `<owner>-<repo>-<sha>/` directory, stripped).
 * Input `{ repo: "owner/name", sha }` is a plain schema, so the stage needs a mapper.
 */
export function gitHubSource<const N extends string, const T extends string = never>(
  def: GitHubSourceDef<N, T>,
): Step<N, GitHubSourceInput, typeof Source, T> & LibraryStep {
  return makeStep(
    "gitHubSource",
    { name: def.name, image: GITHUB_SOURCE_IMAGE, input, output: Source, secrets: def.token ? [def.token] : [], timeoutSeconds: def.timeoutSeconds },
    {
      manifestConfig: () => ({ ...(def.token ? { token: def.token.name } : {}) }),
      async run({ repo, sha }: { repo: string; sha: string }, ctx: Ctx) {
        const headers: Record<string, string> = { accept: "application/vnd.github+json", "user-agent": "olve-pipelines" };
        if (def.token) headers.authorization = `Bearer ${ctx.secrets[def.token.name]}`;
        const url = gitHubTarballUrl(repo, sha);
        mkdirSync(ctx.tmpDir, { recursive: true });
        const dir = mkdtempSync(join(ctx.tmpDir, "github-source-"));
        const archive = join(dir, "repo.tar.gz");

        ctx.log("fetching source", { repo, sha });
        // Retries network errors and 5xx; a 4xx (bad sha, no access) fails at once.
        const status = await ctx.retry({ attempts: 4, delaySeconds: 2, backoff: 2 }, async () => {
          const res = await ctx.http.fetch(url, { headers });
          if (res.status >= 500) throw new Error(`HTTP ${res.status} from ${url}`);
          if (res.ok) await Bun.write(archive, res);
          return res.status;
        });
        if (status < 200 || status >= 300) {
          throw new Error(`cannot fetch ${repo}@${sha}: HTTP ${status} from ${url}${def.token ? "" : " (no token configured: a private repository needs one)"}`);
        }

        const extracted = join(dir, "x");
        mkdirSync(extracted);
        await extractTar(archive, extracted);
        // GitHub tarballs hold one top-level directory, plus a pax_global_header entry (the commit id)
        // that some extractors materialise as a file.
        const roots = readdirSync(extracted).filter((e) => e !== "pax_global_header" && statSync(join(extracted, e)).isDirectory());
        if (roots.length !== 1) throw new Error(`unexpected tarball layout for ${repo}@${sha}: top-level entries ${JSON.stringify(readdirSync(extracted))}`);
        const tree = await ctx.output.dir(join(extracted, roots[0]!), { name: "tree" });
        return { repo, sha, tree };
      },
    },
  ) as never;
}
