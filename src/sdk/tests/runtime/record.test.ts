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
  test('null, booleans, single-key objects and null-prototype objects record as values', () => {
    const bare = Object.assign(Object.create(null), { k: 1 });
    expect(
      recordMapper(
        (r: R) => ({ z: null, t: true, f: false, one: { a: r.input.sha }, bare }),
        roots,
      ),
    ).toEqual({
      z: { const: null },
      t: { const: true },
      f: { const: false },
      one: { a: { ref: 'input.sha' } },
      bare: { k: { const: 1 } },
    });
  });
  const LITERALS =
    'mappers may only use property access and literals (no template strings, arithmetic, method calls or logic) — move computation into the handler';
  const refused: [string, (r: R) => unknown, string, Set<string>?][] = [
    ['undefined', () => ({ o: { p: undefined } }), "mapper returns undefined at 'o.p'"],
    ['a function', () => ({ a: () => 1 }), "mapper returns a function at 'a'"],
    ['NaN', () => ({ a: [1, NaN] }), "mapper returns a non-finite number at 'a[1]'"],
    ['Infinity', () => ({ a: Infinity }), "mapper returns a non-finite number at 'a'"],
    [
      'a bigint',
      () => ({ a: 1n }),
      "mapper returns an unsupported value (bigint) at 'a'",
    ],
    [
      'a reference in an array',
      (r) => ({ a: [1, r.build] }),
      "mapper puts references inside an array at 'a' (only constant arrays are supported)",
    ],
    ['a Map', () => ({ a: new Map() }), "mapper returns a non-plain object at 'a'"],
    ['a Date', () => ({ a: new Date(0) }), "mapper returns a non-plain object at 'a'"],
    [
      'a field name with a dot',
      (r) => ({ a: r.input['a.b'] }),
      `mapper accesses field 'a.b' (field names containing '.' cannot be mapped) on reference 'input'; ${LITERALS}`,
    ],
    [
      'an unavailable root',
      (r) => ({ a: r['deploy-prod'] }),
      "mapper references 'deploy-prod', which is not available at this stage (available: build, checkout, deploy-beta, input)",
    ],
    [
      'any root before the first stage',
      (r) => ({ a: r.input }),
      "mapper references 'input', which is not available at this stage (available: none)",
      new Set(),
    ],
    ['its whole argument', (r) => ({ a: r }), 'mapper enumerates/spreads its argument'],
    ['a non-object result', (r) => r.build, 'mapper must return an object literal'],
  ];
  test.each(refused)('refuses %s', (_, fn, message, available = roots) =>
    expect(() => recordMapper(fn as never, available)).toThrow(message),
  );
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
