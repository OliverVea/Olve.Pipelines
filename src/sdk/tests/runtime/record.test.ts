import { describe, expect, test } from 'bun:test';
import {
  MapperError,
  recordMapper,
  refPathOf,
  rootProxy,
} from '../../src/describe/record.ts';

const roots = new Set(['input', 'checkout', 'build', 'deploy-beta']);
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type R = any;

describe('recording', () => {
  test('refs, constants and nested objects use the tagged encoding', () => {
    expect(
      recordMapper(
        (r: R) => ({
          source: r.checkout,
          sha: r.input.sha,
          n: 3,
          url: r['deploy-beta'].url,
          meta: { repo: r.input.repo, tag: 'x', list: [1, 'a'] },
        }),
        roots,
      ),
    ).toEqual({
      source: { ref: 'checkout' },
      sha: { ref: 'input.sha' },
      n: { const: 3 },
      url: { ref: 'deploy-beta.url' },
      meta: {
        repo: { ref: 'input.repo' },
        tag: { const: 'x' },
        list: { const: [1, 'a'] },
      },
    });
  });
  test('objects whose only key is ref/const are rejected (ambiguous with the encoding)', () => {
    expect(() => recordMapper((r: R) => ({ x: { ref: r.input.sha } }), roots)).toThrow(
      /reserved by the manifest/,
    );
    expect(() => recordMapper(() => ({ x: { const: 1 } }), roots)).toThrow(
      /reserved by the manifest/,
    );
    expect(
      recordMapper((r: R) => ({ x: { ref: r.input.sha, other: 1 } }), roots),
    ).toEqual({ x: { ref: { ref: 'input.sha' }, other: { const: 1 } } });
  });
  test('undefined, functions, refs in arrays, non-object results', () => {
    expect(() => recordMapper(() => ({ a: undefined }), roots)).toThrow(/undefined/);
    expect(() => recordMapper(() => ({ a: () => 1 }), roots)).toThrow(/function/);
    expect(() => recordMapper((r: R) => ({ a: [r.build] }), roots)).toThrow(
      /inside an array/,
    );
    expect(() => recordMapper((r: R) => r.build, roots)).toThrow(/object literal/);
  });
});

describe('proxy throws on what it can see', () => {
  const cases: [string, (r: R) => unknown][] = [
    ['template string', (r) => ({ tag: `v${r.input.sha}` })],
    ['concatenation', (r) => ({ tag: 'v' + r.input.sha })],
    ['String()', (r) => ({ tag: String(r.input.sha) })],
    ['method call', (r) => ({ tag: r.input.sha.slice(0, 7) })],
    ['spread', (r) => ({ ...r.checkout })],
    ['JSON.stringify', (r) => ({ s: JSON.stringify(r.input) })],
    ['arithmetic', (r) => ({ n: r.input.count * 2 })],
    ['unavailable root', (r) => ({ x: r['deploy-prod'] })],
    ['in operator', (r) => ({ x: 'sha' in r.input })],
  ];
  for (const [name, fn] of cases)
    test(name, () => expect(() => fn(rootProxy(roots))).toThrow(MapperError));
});

describe('proxy limits (why mapper-source analysis exists)', () => {
  test('a ternary on a ref silently picks the truthy branch', () => {
    const rec = ((r: R) => ({ src: r.input.publishCli ? r.build : r.checkout }))(
      rootProxy(roots),
    ) as R;
    expect(refPathOf(rec.src)).toBe('build');
  });
  test('=== against a value is silently false', () => {
    const rec = ((r: R) => ({ prod: r.input.deployTo === 'prod' }))(
      rootProxy(roots),
    ) as R;
    expect(rec.prod).toBe(false);
  });
});
