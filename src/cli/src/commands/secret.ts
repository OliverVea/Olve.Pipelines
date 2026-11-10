import { secretsCopy, secretsDelete, secretsList, secretsSet } from '@olve/api';
import { resolvePipeline } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { table } from '../output';
import type { Command, CommandContext } from '../registry';

export const trailingNewlineWarning = (value: string): string | undefined =>
  value.endsWith('\n') && !value.replace(/[\r\n]+$/, '').includes('\n')
    ? 'warning: the value ends with a newline, which is kept and becomes part of the secret; pass --trim to drop it (or pipe with `echo -n`).'
    : undefined;

/** The value: `--from-env`, `--from-file`, piped stdin, or a hidden prompt on a terminal. */
async function readValue(ctx: CommandContext, name: string): Promise<string> {
  const fromEnv = ctx.args.option('from-env');
  const fromFile = ctx.args.option('from-file');
  if (fromEnv !== undefined && fromFile !== undefined)
    throw new CliError('Specify only one of --from-env or --from-file.');
  if (fromEnv !== undefined) {
    const value = ctx.env[fromEnv];
    if (value === undefined)
      throw new CliError(`Environment variable '${fromEnv}' is not set.`);
    return value;
  }
  if (fromFile !== undefined) {
    const file = Bun.file(fromFile);
    if (!(await file.exists())) throw new CliError(`File '${fromFile}' not found.`);
    return file.text();
  }
  if (ctx.stdinIsTerminal) {
    const typed = await ctx.prompt(`Value for '${name}': `, { hidden: true });
    if (!typed) throw new CliError('No secret value entered.');
    return typed;
  }
  const piped = await ctx.stdin();
  if (!piped)
    throw new CliError(
      'No secret value received on stdin (use --from-env/--from-file, or pipe a value).',
    );
  return piped;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/** `--expires`: a date (`2027-01-25`, midnight UTC), a timestamp, or days from now (`90d`). */
export function parseExpiry(text: string, now: Date): Date {
  const days = /^(\d+)d$/.exec(text);
  const at = days
    ? new Date(now.getTime() + Number(days[1]) * DAY_MS)
    : new Date(/^\d{4}-\d{2}-\d{2}$/.test(text) ? `${text}T00:00:00Z` : text);
  if (Number.isNaN(at.getTime()))
    throw new CliError(
      `--expires must be a date (2027-01-25), a timestamp or a number of days (90d), not '${text}'.`,
    );
  if (at.getTime() <= now.getTime())
    throw new CliError(`--expires ${text} is not in the future.`);
  return at;
}

const day = (iso: string) => iso.slice(0, 10);

/** `<source-pipeline>[/<name>]`. */
export function splitSource(text: string): { pipeline: string; name?: string } {
  const slash = text.lastIndexOf('/');
  return slash <= 0 || slash === text.length - 1
    ? { pipeline: text.replace(/\/+$/, '') }
    : { pipeline: text.slice(0, slash), name: text.slice(slash + 1) };
}

export const secretCommands: Command[] = [
  {
    noun: 'secret',
    verb: 'list',
    operands: 1,
    summary: "List a pipeline's secrets: who declares each and whether it is set",
    detail: `pl secret list <pipeline>

  Every secret the active version declares (by step, onFailure handler or poll trigger) or
  the binding uses, plus any set but unused. Values are never returned.`,
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const secrets = await call(
        secretsList({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(secrets, () => {
        if (secrets.length === 0) {
          ctx.out.line('(no secrets)');
          return;
        }
        const now = ctx.now().getTime();
        ctx.out.lines(
          table(
            ['NAME', 'SET', 'EXPIRES', 'USED BY', 'DESCRIPTION'],
            secrets.map((s) => [
              s.name,
              s.isSet === true
                ? 'set'
                : s.isSet === false
                  ? s.needed === false
                    ? 'not needed'
                    : 'unset'
                  : 'unknown',
              s.expiresAt
                ? `${day(s.expiresAt)}${Date.parse(s.expiresAt) <= now ? ' (expired)' : ''}`
                : '',
              s.declaredBy.length === 0 ? '(unused)' : s.declaredBy.join(', '),
              s.description ?? '',
            ]),
          ),
        );
      });
    },
  },
  {
    noun: 'secret',
    verb: 'set',
    operands: 2,
    options: ['from-env', 'from-file', 'expires'],
    flags: ['trim'],
    summary: 'Set a secret value (from stdin by default)',
    detail: `pl secret set <pipeline> <name> [options]

  Reads the value from stdin by default, so it stays out of shell history and
  the process list. The value is sent verbatim, so multi-line secrets such as
  PEM keys round-trip intact; a one-line value piped with a trailing newline
  (\`echo $TOKEN | …\`) gets a warning.

  --from-env <VAR>    Read the value from environment variable VAR
  --from-file <path>  Read the value from a file (verbatim)
  --trim              Drop trailing newlines from the value
  --expires <when>    When the value expires: a date (2027-01-25), a timestamp or
                      days from now (90d). The pipeline reports it from 14 days
                      before; nothing is deleted. Setting a value again without
                      --expires clears it.

  Examples:
    echo -n "$TOKEN" | pl secret set <pipeline> REGISTRY_TOKEN
    pl secret set <pipeline> REGISTRY_TOKEN --from-env TOKEN
    pl secret set <pipeline> DEPLOY_KEY --from-file ./id_ed25519
    pl secret set <pipeline> NPM_TOKEN --from-env NPM_TOKEN --expires 90d`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const name = ctx.args.operand(1)!;
      const expires = ctx.args.option('expires');
      const expiresAt =
        expires === undefined ? undefined : parseExpiry(expires, ctx.now());
      let value = await readValue(ctx, name);
      if (ctx.args.flag('trim')) value = value.replace(/[\r\n]+$/, '');
      else {
        const warning = trailingNewlineWarning(value);
        if (warning) ctx.stderr(warning);
      }
      await call(
        secretsSet({
          client: ctx.api,
          path: { pipelineId: id, name },
          body: { value, ...(expiresAt ? { expiresAt: expiresAt.toISOString() } : {}) },
        }),
      );
      if (!ctx.json)
        ctx.out.line(
          `Set secret '${name}' for pipeline ${label}${expiresAt ? ` (expires ${day(expiresAt.toISOString())})` : ''}.`,
        );
    },
  },
  {
    noun: 'secret',
    verb: 'delete',
    operands: 2,
    flags: ['force'],
    summary: 'Delete a secret by name',
    detail: `pl secret delete <pipeline> <name> [--force]

  A secret the current config references (a secretRef) is only deleted with --force; new
  runs then wait until it is set again.`,
    async run(ctx) {
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const name = ctx.args.operand(1)!;
      await call(
        secretsDelete({
          client: ctx.api,
          path: { pipelineId: id, name },
          query: { force: ctx.args.flag('force') || undefined },
        }),
      );
      if (!ctx.json) ctx.out.line(`Deleted secret '${name}' from pipeline ${label}.`);
    },
  },
  {
    noun: 'secret',
    verb: 'copy',
    operands: 2,
    options: ['name'],
    flags: ['overwrite'],
    summary: 'Copy a secret (or all) from another pipeline, server-side',
    detail: `pl secret copy <source-pipeline>[/<name>] <target-pipeline> [options]

  Copies a secret value from one pipeline to another on the same instance. The copy is
  done by the service; the value is never returned or printed. Without /<name> every
  secret of the source is copied. Pipelines by id or name.

  Caveat: anyone who can write the target pipeline can read the copied value from its steps.

  --name <target-name>   Store the copy under a different name (single secret only)
  --overwrite            Replace secrets the target already has (default: refuse)

  Examples:
    pl secret copy "Olve.Pipelines v2"/GITHUB_TOKEN my-app
    pl secret copy shared-creds my-app --overwrite
    pl secret copy my-app/GITHUB_TOKEN my-app --name GH_READ_TOKEN`,
    async run(ctx) {
      const source = splitSource(ctx.args.operand(0)!);
      const targetName = ctx.args.option('name');
      if (targetName !== undefined && source.name === undefined)
        throw new CliError(
          '--name needs a single source secret (<source-pipeline>/<name>).',
        );
      const from = await resolvePipeline(ctx.api, source.pipeline);
      const to = await resolvePipeline(ctx.api, ctx.args.operand(1)!);
      const response = await call(
        secretsCopy({
          client: ctx.api,
          path: { pipelineId: to.id },
          body: {
            sourcePipelineId: from.id,
            names: source.name === undefined ? null : [source.name],
            targetName: targetName ?? null,
            overwrite: ctx.args.flag('overwrite'),
          },
        }),
      );
      ctx.out.emit(response, () =>
        ctx.out.line(
          response.copied.length === 0
            ? `Nothing to copy from ${from.label}.`
            : `Copied ${response.copied.join(', ')} from ${from.label} to ${to.label}.`,
        ),
      );
    },
  },
];
