import { describe, expect, test } from 'vitest';
import { diffLines, foldDiff } from '../src/model/diff.js';
import { formFields, parseField, constraintHint } from '../src/model/schema-form.js';
import {
  documentSchema,
  propertiesAt,
  validate,
  valuesAt,
  type ConfigEntry,
} from '../src/model/config-schema.js';
import type { JsonSchema } from '../src/model/manifest.js';

const render = (a: string, b: string) =>
  diffLines(a, b).map((l) => `${{ same: ' ', add: '+', del: '-' }[l.kind]}${l.text}`);

describe('diffLines', () => {
  test.each([
    ['identical', 'a\nb', 'a\nb', [' a', ' b']],
    ['a changed middle line', 'a\nb\nc', 'a\nx\nc', [' a', '-b', '+x', ' c']],
    ['an added line', 'a\nc', 'a\nb\nc', [' a', '+b', ' c']],
    ['a removed line', 'a\nb\nc', 'a\nc', [' a', '-b', ' c']],
    ['all different', 'a', 'b', ['-a', '+b']],
  ])('%s', (_, a, b, expected) => expect(render(a, b)).toEqual(expected));

  test('numbers lines on both sides', () => {
    expect(diffLines('a\nb\nc', 'a\nc\nd')).toEqual([
      { kind: 'same', text: 'a', a: 1, b: 1 },
      { kind: 'del', text: 'b', a: 2 },
      { kind: 'same', text: 'c', a: 3, b: 2 },
      { kind: 'add', text: 'd', b: 3 },
    ]);
  });
});

describe('foldDiff', () => {
  const lines = (n: number, changeAt: number) =>
    diffLines(
      Array.from({ length: n }, (_, i) => `l${i}`).join('\n'),
      Array.from({ length: n }, (_, i) => (i === changeAt ? 'changed' : `l${i}`)).join(
        '\n',
      ),
    );

  test.each([
    [
      'a change in the middle keeps 3 lines either side',
      20,
      10,
      3,
      [{ skipped: 7 }, 8, { skipped: 6 }],
    ],
    ['a change at the start', 10, 0, 3, [5, { skipped: 6 }]],
    ['context 0 keeps only the change', 5, 2, 0, [{ skipped: 2 }, 2, { skipped: 2 }]],
    ['no change folds everything', 4, -1, 3, [{ skipped: 4 }]],
  ])('%s', (_, n, at, context, expected) => {
    const hunks = foldDiff(lines(n, at), context).map((h) =>
      'skipped' in h ? h : h.lines.length,
    );
    expect(hunks).toEqual(expected);
  });
});

describe('schema-form', () => {
  const schema: JsonSchema = {
    type: 'object',
    required: ['name', 'port', 'mode'],
    properties: {
      name: { type: 'string', minLength: 2, maxLength: 5, pattern: '^[a-z]+$' },
      port: { type: 'integer', minimum: 1, maximum: 65535 },
      ratio: { type: 'number', exclusiveMinimum: 0, exclusiveMaximum: 1 },
      mode: { enum: ['a', 'b'] },
      debug: { type: 'boolean', default: false },
      extra: { type: 'object' },
    },
  };
  const fields = Object.fromEntries(formFields(schema).map((f) => [f.name, f]));

  test('kinds, required and defaults', () => {
    expect(
      Object.values(fields).map((f) => [f.name, f.kind, f.required, f.hasDefault]),
    ).toEqual([
      ['name', 'string', true, false],
      ['port', 'integer', true, false],
      ['ratio', 'number', false, false],
      ['mode', 'enum', true, false],
      ['debug', 'boolean', false, true],
      ['extra', 'json', false, false],
    ]);
  });

  test.each([
    ['name', '', { ok: false, error: 'Required.' }],
    ['name', 'a', { ok: false, error: 'At least 2 characters.' }],
    ['name', 'abcdef', { ok: false, error: 'At most 5 characters.' }],
    ['name', 'AB', { ok: false, error: 'Must match ^[a-z]+$.' }],
    ['name', 'abc', { ok: true, value: 'abc', empty: false }],
    ['port', 'x', { ok: false, error: 'Not a number.' }],
    ['port', '1.5', { ok: false, error: 'Must be a whole number.' }],
    ['port', '0', { ok: false, error: 'At least 1.' }],
    ['port', '70000', { ok: false, error: 'At most 65535.' }],
    ['port', ' 80 ', { ok: true, value: 80, empty: false }],
    ['ratio', '0', { ok: false, error: 'Greater than 0.' }],
    ['ratio', '1', { ok: false, error: 'Less than 1.' }],
    ['ratio', '', { ok: true, value: undefined, empty: true }],
    ['mode', '"b"', { ok: true, value: 'b', empty: false }],
    ['debug', 'true', { ok: true, value: true, empty: false }],
    ['debug', '', { ok: true, value: undefined, empty: true }],
    ['extra', '{', { ok: false, error: 'Not valid JSON.' }],
    ['extra', '{"a":1}', { ok: true, value: { a: 1 }, empty: false }],
  ])('%s %j', (name, text, expected) =>
    expect(parseField(fields[name], text)).toEqual(expected),
  );

  test.each([
    ['name', 'pattern ^[a-z]+$, min length 2, max length 5'],
    ['port', '≥ 1, ≤ 65535'],
    ['ratio', '> 0, < 1'],
    ['debug', 'default false'],
  ])('hint for %s', (name, hint) => expect(constraintHint(fields[name])).toBe(hint));
});

describe('config-schema validate', () => {
  const target: JsonSchema = {
    oneOf: [
      {
        type: 'object',
        required: ['kind', 'host'],
        properties: { kind: { const: 'ssh' }, host: { type: 'string', minLength: 1 } },
        additionalProperties: false,
      },
      {
        type: 'object',
        required: ['kind'],
        properties: { kind: { const: 'none' } },
        additionalProperties: false,
      },
    ],
  };
  const entries: ConfigEntry[] = [
    {
      name: 'target',
      description: 'Where it deploys.',
      required: true,
      schema: target,
    },
    {
      name: 'build',
      description: '',
      required: false,
      schema: {
        type: 'object',
        properties: {
          runtime: { enum: ['jit', 'aot'], default: 'jit' },
          keys: { type: 'object', additionalProperties: { type: 'string' } },
        },
      },
    },
  ];
  const doc = documentSchema(entries);

  test.each([
    ['a valid document', { target: { kind: 'none' } }, []],
    ['a missing required entry', {}, ['target: is required']],
    ['unknown top-level keys are ignored', { target: { kind: 'none' }, other: 1 }, []],
    [
      'the branch picked by its discriminator reports its own issues',
      { target: { kind: 'ssh' } },
      ['target.host: is required'],
    ],
    [
      'no branch picked',
      { target: { kind: 'k8s' } },
      ['target: must be one of: kind: "ssh" | kind: "none"'],
    ],
    [
      'an unknown property in the picked branch',
      { target: { kind: 'none', host: 'x' } },
      ['target.host: is not allowed (unknown property)'],
    ],
    [
      'a wrong enum value',
      { target: { kind: 'none' }, build: { runtime: 'native' } },
      ['build.runtime: must be one of "jit", "aot"'],
    ],
    [
      'a record value of the wrong type',
      { target: { kind: 'none' }, build: { keys: { a: 1 } } },
      ['build.keys.a: expected string, got integer'],
    ],
  ])('%s', (_, value, expected) => {
    const { issues } = validate(doc, value);
    expect(issues.map((i) => `${i.path.join('.')}: ${i.message}`)).toEqual(expected);
  });

  test('defaults are filled before required, without changing the input', () => {
    const input = { target: { kind: 'none' }, build: {} };
    expect(validate(doc, input).value).toEqual({
      target: { kind: 'none' },
      build: { runtime: 'jit' },
    });
    expect(input.build).toEqual({});
  });

  test('completion: properties and values at a path, across union branches', () => {
    expect([...propertiesAt(doc, ['target']).keys()]).toEqual(['kind', 'host']);
    expect(valuesAt(doc, ['target', 'kind'])).toEqual(['ssh', 'none']);
    expect(valuesAt(doc, ['build', 'runtime'])).toEqual(['jit', 'aot']);
  });
});
