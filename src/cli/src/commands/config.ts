import {
  configCheck,
  configGet,
  configHistory,
  configRollback,
  configSet,
  type ConfigCheck,
  type ConfigSaveResult,
} from '@olve/api';
import { resolvePipeline } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { time } from '../format';
import { table, type Output } from '../output';
import type { Command } from '../registry';

function intOption(text: string | undefined, name: string): number | undefined {
  if (text === undefined) return undefined;
  const digits = text.replace(/^[v#]/, '');
  if (!/^\d+$/.test(digits))
    throw new CliError(`--${name} must be a number, got '${text}'.`);
  return Number(digits);
}

const reasons = (c: ConfigCheck) => [
  ...c.problems.map((p) => `config ${p}`),
  ...c.missingSecrets.map((s) => `secret ${s} not set`),
  ...c.unmet,
];

export function printSaved(out: Output, label: string, saved: ConfigSaveResult): void {
  out.line(
    `Saved config #${saved.snapshot.number} of ${label}${saved.snapshot.restoredFrom != null ? ` (content of #${saved.snapshot.restoredFrom})` : ''}.`,
  );
  if (saved.activatedVersion != null)
    out.line(
      `Activated v${saved.activatedVersion}: its config and secrets are satisfied.`,
    );
  if (saved.active && !saved.active.satisfied)
    out.line(`active v${saved.active.version}: ${reasons(saved.active).join('; ')}`);
  if (saved.pending) {
    out.line(
      `pending v${saved.pending.version}: ${saved.pending.satisfied ? 'satisfied' : reasons(saved.pending).join('; ')}`,
    );
    for (const d of saved.pending.changedDefaults)
      out.line(
        `  v${saved.pending.version} changes the default of ${d.path} (${d.from} → ${d.to})`,
      );
  }
}

export function printCheck(out: Output, c: ConfigCheck): void {
  out.line(
    `v${c.version}: ${c.satisfied ? 'satisfied' : !c.valid ? 'invalid' : c.missingSecrets.length ? 'valid, but secrets are missing' : 'valid, but not satisfied'}`,
  );
  for (const problem of c.problems) out.line(`  config ${problem}`);
  for (const secret of c.missingSecrets) out.line(`  secret ${secret} not set`);
  for (const unmet of c.unmet) out.line(`  ${unmet}`);
  for (const d of c.changedDefaults)
    out.line(`  v${c.version} changes the default of ${d.path} (${d.from} → ${d.to})`);
  if (c.referencedSecrets.length) out.line(`secrets:  ${c.referencedSecrets.join(', ')}`);
  if (c.effective != null) out.line(`effective: ${c.effective}`);
}

export const configCommands: Command[] = [
  {
    noun: 'config',
    verb: 'get',
    operands: 1,
    options: ['snapshot'],
    summary: "Print the pipeline's config document (YAML) or an older snapshot",
    detail: `pl config get <pipeline> [--snapshot N] > app.yml

  Prints the current config snapshot (or snapshot N) as YAML, exactly as it was saved.
  Defaults are not written into it; see \`pl config check\` for the effective config.`,
    async run(ctx) {
      const snapshot = intOption(ctx.args.option('snapshot'), 'snapshot');
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const doc = await call(
        configGet({ client: ctx.api, path: { pipelineId: id }, query: { snapshot } }),
      );
      ctx.out.emit(doc, () => {
        if (!doc.snapshot)
          ctx.stderr(`${label} has no config yet (pl config set ${label} -f <file>).`);
        else if (doc.yaml.length) ctx.out.line(doc.yaml.replace(/[\r\n]+$/, ''));
      });
    },
  },
  {
    noun: 'config',
    verb: 'set',
    operands: 1,
    options: ['file'],
    aliases: { f: 'file' },
    summary: 'Validate a YAML config document and save it as a new snapshot',
    detail: `pl config set <pipeline> -f app.yml     (or pipe the YAML on stdin)

  Saves the whole document as the next snapshot (config #N). It must be valid for the
  active version, so a typo can't break what is running; keys the active version
  doesn't declare are kept (config for a future version can be saved first). The
  result for a pending version is shown too, and that version activates as soon as
  its config and secrets are satisfied.`,
    async run(ctx) {
      const file = ctx.args.option('file');
      let yaml: string;
      if (file !== undefined) {
        const f = Bun.file(file);
        if (!(await f.exists())) throw new CliError(`File '${file}' not found.`);
        yaml = await f.text();
      } else if (!ctx.stdinIsTerminal) yaml = await ctx.stdin();
      else throw new CliError('Give the document with -f <file>, or pipe it on stdin.');
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const saved = await call(
        configSet({ client: ctx.api, path: { pipelineId: id }, body: { yaml } }),
      );
      ctx.out.emit(saved, () => printSaved(ctx.out, label, saved));
    },
  },
  {
    noun: 'config',
    verb: 'history',
    operands: 1,
    summary: 'List the config snapshots: number, author, time',
    detail: 'pl config history <pipeline>',
    async run(ctx) {
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const snapshots = await call(
        configHistory({ client: ctx.api, path: { pipelineId: id } }),
      );
      ctx.out.emit(snapshots, () => {
        if (snapshots.length === 0) {
          ctx.out.line('(no config saved)');
          return;
        }
        ctx.out.lines(
          table(
            ['CONFIG', 'AUTHOR', 'SAVED', 'NOTE'],
            snapshots.map((s) => [
              `#${s.number}`,
              s.author,
              time(s.createdAt),
              s.restoredFrom != null ? `rollback to #${s.restoredFrom}` : '',
            ]),
          ),
        );
      });
    },
  },
  {
    noun: 'config',
    verb: 'rollback',
    operands: 2,
    summary: 'Save an old config snapshot again as the newest one',
    detail: `pl config rollback <pipeline> <N>

  Saves snapshot N's content as a new snapshot (#latest+1), validated like \`pl config set\`.
  Runs that already started keep the snapshot they pinned.`,
    async run(ctx) {
      const text = ctx.args.operand(1)!;
      if (!/^#?\d+$/.test(text))
        throw new CliError(`'${text}' is not a snapshot number.`);
      const { id, label } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const saved = await call(
        configRollback({
          client: ctx.api,
          path: { pipelineId: id },
          body: { snapshot: Number(text.replace(/^#/, '')) },
        }),
      );
      ctx.out.emit(saved, () => printSaved(ctx.out, label, saved));
    },
  },
  {
    noun: 'config',
    verb: 'check',
    operands: 1,
    options: ['version'],
    summary:
      'Validate the current config against a version (default: pending, else active)',
    detail: `pl config check <pipeline> [--version V]

  Checks the current config document against version V (default: the pending version,
  else the active one): problems, secrets it references that are not set, defaults V
  changes that the document relies on, and the effective config (defaults applied).
  Exits 1 when the version could not activate with this config.`,
    async run(ctx) {
      const version = intOption(ctx.args.option('version'), 'version');
      const { id } = await resolvePipeline(ctx.api, ctx.args.operand(0)!);
      const check = await call(
        configCheck({ client: ctx.api, path: { pipelineId: id }, query: { version } }),
      );
      ctx.out.emit(check, () => printCheck(ctx.out, check));
      if (!check.satisfied)
        throw new CliError(`Version ${check.version} can't activate with this config.`);
    },
  },
];
