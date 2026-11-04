import { describe, expect, test } from 'vitest';
import {
  branchOf,
  editYaml,
  kindOf,
  secretNameOf,
  seedOf,
  switchBranch,
  unionOf,
  yamlValue,
} from '../src/model/config-form.js';
import type { JsonSchema } from '../src/model/manifest.js';

const secret: JsonSchema = {
  type: 'object',
  properties: { secret: { type: 'string', const: 'GHCR_TOKEN' } },
  required: ['secret'],
  additionalProperties: false,
  'x-olve-kind': 'secretRef',
};

const target: JsonSchema = {
  oneOf: [
    {
      type: 'object',
      required: ['kind', 'host'],
      properties: {
        kind: { type: 'string', const: 'ssh' },
        host: { type: 'string' },
        namespace: { type: 'string', default: 'apps' },
      },
    },
    {
      type: 'object',
      required: ['kind'],
      properties: {
        kind: { type: 'string', const: 'inCluster' },
        namespace: { type: 'string', default: 'apps' },
      },
    },
    { type: 'object', required: ['kind'], properties: { kind: { const: 'none' } } },
  ],
};

describe('kindOf', () => {
  test.each([
    ['a secret reference', secret, 'secret'],
    ['a discriminated union', target, 'union'],
    [
      'a union without a discriminator',
      { anyOf: [{ type: 'string' }, { type: 'number' }] },
      'json',
    ],
    ['an enum', { type: 'string', enum: ['aot', 'jit'] }, 'enum'],
    ['a const', { const: 'x' }, 'enum'],
    ['an object', { type: 'object', properties: { a: { type: 'string' } } }, 'object'],
    ['a record', { type: 'object', additionalProperties: { type: 'string' } }, 'record'],
    ['a free object', { type: 'object' }, 'json'],
    ['a list', { type: 'array', items: { type: 'string' } }, 'list'],
    ['a nullable string', { type: ['string', 'null'] }, 'string'],
    ['an integer', { type: 'integer' }, 'integer'],
    ['a boolean', { type: 'boolean' }, 'boolean'],
    ['no type', {}, 'json'],
  ] as [string, JsonSchema, string][])('%s', (_, schema, kind) =>
    expect(kindOf(schema)).toBe(kind),
  );

  test('the secret a reference names', () =>
    expect(secretNameOf(secret)).toBe('GHCR_TOKEN'));
});

describe('unions', () => {
  const u = unionOf(target)!;

  test('the discriminator and its options', () => {
    expect(u.discriminator).toBe('kind');
    expect(u.options.map((o) => o.value)).toEqual(['ssh', 'inCluster', 'none']);
  });

  test.each([
    [{ kind: 'inCluster' }, 'inCluster'],
    [{ kind: 'unknown' }, 'ssh'],
    [undefined, 'ssh'],
  ])('%j follows %s', (value, kind) =>
    expect(branchOf(u, value).properties?.kind.const).toBe(kind),
  );

  test.each([
    [
      'keeps the fields the new branch has',
      { kind: 'ssh', host: 'h', namespace: 'x' },
      'inCluster',
      { kind: 'inCluster', namespace: 'x' },
    ],
    [
      'seeds the required fields of the new branch',
      { kind: 'none' },
      'ssh',
      { kind: 'ssh', host: '' },
    ],
    [
      'drops what the new branch lacks',
      { kind: 'ssh', host: 'h' },
      'none',
      { kind: 'none' },
    ],
  ])('switching %s', (_, current, value, expected) =>
    expect(switchBranch(u, current, value)).toEqual(expected),
  );
});

describe('seedOf', () => {
  test.each([
    ['a default wins', { type: 'string', default: 'apps' }, 'apps'],
    ['a union seeds its first branch', target, { kind: 'ssh', host: '' }],
    ['a secret reference names its secret', secret, { secret: 'GHCR_TOKEN' }],
    ['an enum takes its first value', { enum: ['aot', 'jit'] }, 'aot'],
    ['a number its minimum', { type: 'integer', minimum: 1 }, 1],
    ['a list is empty', { type: 'array', items: { type: 'string' } }, []],
    [
      'a record is empty',
      { type: 'object', additionalProperties: { type: 'string' } },
      {},
    ],
  ] as [string, JsonSchema, unknown][])('%s', (_, schema, seed) =>
    expect(seedOf(schema)).toEqual(seed),
  );
});

describe('editYaml', () => {
  const text = `# where it deploys
target:
  prod:
    kind: ssh # the homelab box
    host: oliver@bulwark-m2
build:
  runtime: jit
`;

  test('sets a nested value and keeps every comment', () => {
    const out = editYaml(text, ['target', 'prod', 'host'], 'oliver@new');
    expect(out).toContain('# where it deploys');
    expect(out).toContain('kind: ssh # the homelab box');
    expect(yamlValue(out)).toEqual({
      target: { prod: { kind: 'ssh', host: 'oliver@new' } },
      build: { runtime: 'jit' },
    });
  });

  test('a changed scalar keeps its inline comment', () =>
    expect(editYaml(text, ['target', 'prod', 'kind'], 'inCluster')).toContain(
      'kind: inCluster # the homelab box',
    ));

  test('creates missing parents', () =>
    expect(
      yamlValue(editYaml(text, ['publish', 'npm', 'enabled'], true)).publish,
    ).toEqual({
      npm: { enabled: true },
    }));

  test('undefined removes the value', () =>
    expect(yamlValue(editYaml(text, ['build', 'runtime'], undefined)).build).toEqual({}));

  test('an empty document becomes a mapping', () =>
    expect(yamlValue(editYaml('', ['build'], { runtime: 'aot' }))).toEqual({
      build: { runtime: 'aot' },
    }));

  test('list items by index', () =>
    expect(yamlValue(editYaml('users: [a, b]\n', ['users', 1], 'c')).users).toEqual([
      'a',
      'c',
    ]));
});
