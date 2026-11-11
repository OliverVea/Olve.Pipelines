import {
  conditionText,
  str,
  type DevManifest,
  type DevStep,
  type Json,
  type JsonObject,
  type StageNode,
} from './manifest';
import { outcomeStatus, type DevConsole, type StepOutcome } from './session';
import { cellsOf } from './matrix';

type Schema = Record<string, Json>;

/** One line of `pl dev pipeline`'s result table. */
export type StepResult = { step: string; status: string; detail: string | null };

export const exitMeaning = (code: number) =>
  code === 64
    ? ': usage, unknown step'
    : code === 78
      ? ': pipeline invalid'
      : code === 130 || code === 143
        ? ': cancelled'
        : '';
const indent = (text: string) =>
  text
    .split('\n')
    .map((l) => '  ' + l)
    .join('\n');

export function renderEnvelope(
  console: DevConsole,
  step: string,
  outcome: StepOutcome,
): void {
  const e = outcome.envelope;
  if (console.json) {
    console.out(e ? JSON.stringify(e) : 'null');
    return;
  }
  const error =
    e?.error && typeof e.error === 'object' && !Array.isArray(e.error)
      ? e.error
      : undefined;
  switch (outcomeStatus(outcome)) {
    case 'succeeded':
      console.out(`${step}: succeeded`);
      console.out(
        e?.output == null ? '  (no output)' : indent(JSON.stringify(e.output, null, 2)),
      );
      break;
    case 'skipped':
      console.out(`${step}: skipped: ${str(e?.reason) ?? ''}`);
      break;
    case 'failed':
      console.out(
        `${step}: failed (${str(error?.kind) ?? 'handler'}, exit ${outcome.exitCode})`,
      );
      console.out(indent(str(error?.message) ?? ''));
      break;
    default:
      console.out(
        `${step}: no result (exit ${outcome.exitCode}${exitMeaning(outcome.exitCode)}); see the log above`,
      );
  }
  for (const link of Array.isArray(e?.links) ? e.links : []) {
    const l = link && typeof link === 'object' && !Array.isArray(link) ? link : {};
    const url = str(l.url);
    if (url) console.out(`  → ${url}${str(l.label) ? ` (${str(l.label)})` : ''}`);
  }
}

/** The `pl dev describe` summary, also `pl pipeline manifest`'s. */
export function renderManifest(o: (line: string) => void, m: DevManifest): void {
  o(m.description ? `${m.name} — ${m.description}` : m.name);
  o('');
  if (m.inputModel)
    o(`input:    ${m.inputModel} { ${inputFields(m.inputSchema).join(', ')} }`);
  o(`secrets:  ${m.secrets.length ? m.secrets.map((s) => s.name).join(', ') : 'none'}`);
  o(
    `triggers: ${m.triggers.length ? m.triggers.map(triggerLabel).join(', ') : 'none (manual only)'}`,
  );
  o('');
  o('stages:');
  let stage = 0;
  const nodes = (list: StageNode[], ind: string) => {
    for (const node of list) {
      if (node.kind === 'stage') {
        stage++;
        let label = `${stage}.`;
        for (const step of node.steps) {
          o(`${ind}${label.padEnd(4)}${stepLine(step)}`);
          label = '';
        }
      } else if (node.kind === 'when') {
        o(`${ind}when ${node.conditions.map(conditionText).join(' and ')}:`);
        nodes(node.nodes, ind + '  ');
      } else {
        o(`${ind}select on config ${node.select}.${node.discriminator}:`);
        const at = stage;
        for (const [value, caseNodes] of Object.entries(node.cases)) {
          stage = at;
          o(`${ind}  ${value}:`);
          nodes(caseNodes, ind + '    ');
        }
      }
    }
  };
  nodes(m.stages, '  ');
  if (m.failureHandlers.length) {
    o('');
    o('failure handlers:');
    for (const { step, scope } of m.failureHandlers)
      o(`  ${step.name.padEnd(22)} on ${scope ? scope.join(', ') : 'any step'}`);
  }
}

function stepLine(step: DevStep): string {
  let line = `${step.name.padEnd(20)} ${step.image}`;
  if (step.output) line += ` -> ${step.output}`;
  if (step.matrix)
    line += `  [matrix: ${step.matrix.axes.map((a) => a.name).join(' × ')}, ${cellsOf(step.matrix).length} cells]`;
  if (step.secrets.length) line += `  [secrets: ${step.secrets.join(', ')}]`;
  const waits = Object.entries(step.externalInputs);
  if (waits.length)
    line += `  [waits for: ${waits.map(([k, v]) => `${k} (${v})`).join(', ')}]`;
  if (step.preflight) line += '  [preflight]';
  return line;
}

function inputFields(schema: JsonObject | undefined): string[] {
  const required = new Set(Array.isArray(schema?.required) ? schema.required : []);
  const props = (schema?.properties ?? {}) as Record<string, Schema>;
  return Object.entries(props).map(([name, prop]) => {
    let field = required.has(name) ? `${name}*` : name;
    if (prop && 'default' in prop) field += `=${JSON.stringify(prop.default)}`;
    if (Array.isArray(prop?.enum))
      field += ` (${prop.enum.map((v) => (typeof v === 'string' ? v : JSON.stringify(v))).join('|')})`;
    return field;
  });
}

const triggerLabel = (t: JsonObject) =>
  str(t.kind) === 'poll'
    ? `${str(t.name)} (GET ${str(t.url)} every ${t.intervalSeconds}s)`
    : (str(t.name) ?? str(t.kind) ?? '?');

export function renderSummary(console: DevConsole, results: StepResult[]): void {
  if (console.json) {
    console.out(JSON.stringify(results));
    return;
  }
  if (!results.length) return;
  console.out('');
  console.out('result:');
  const width = Math.max(...results.map((r) => r.step.length)) + 2;
  for (const r of results)
    console.out(
      `  ${r.step.padEnd(width)}${r.status.padEnd(10)}${r.detail ?? ''}`.trimEnd(),
    );
}
