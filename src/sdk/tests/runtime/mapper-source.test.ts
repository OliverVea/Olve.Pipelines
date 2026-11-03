import { describe, expect, test } from 'bun:test';
import { analyzeMapperSource } from '../../src/describe/mapper-source.ts';
import { MapperError } from '../../src/describe/record.ts';

const rejection = (src: string) => {
  try {
    analyzeMapperSource(src);
  } catch (e) {
    expect(e).toBeInstanceOf(MapperError);
    return (e as Error).message;
  }
  return undefined;
};
const LITERALS_ONLY = 'mappers may only contain property access and literals';
const op = (what: string) =>
  `mapper uses ${what}; ${LITERALS_ONLY} — move logic into the handler or a when block`;
const notObject = (detail: string) =>
  `mapper source is not an object literal of property accesses and literals (${detail})`;

/* eslint-disable @typescript-eslint/no-explicit-any */
describe('accepted mapper shapes', () => {
  const rows: [string, string][] = [
    [
      'arrow with parenthesised object literal',
      `from => ({ repo: from.input.repo, sha: from.input.sha })`,
    ],
    ['parenthesised param, trailing comma', `(from) => ({ a: from.build, })`],
    [
      'bracket access for hyphenated step names',
      `from => ({ url: from["deploy-beta"].url, b: from['x-y'] })`,
    ],
    ['a numeric bracket key', `from => ({ a: from.build[0] })`],
    [
      'literals, negative numbers, nested objects, constant arrays',
      `from => ({ s: "x", n: -1.5, t: true, f: false, z: null, o: { a: from.input.sha, b: [1, "two"] }, e: [] })`,
    ],
    ['block body with a single return', `(from) => { return { a: from.build }; }`],
    ['function expression', `function (from) { return { a: from.input.sha } }`],
    ['named function expression', `function named(from) { return { a: from.build } }`],
    [
      'destructured parameter',
      `({ input, "deploy-beta": beta }) => ({ sha: input.sha, url: beta.url, input })`,
    ],
    ['renamed destructured parameter', `({ input: i }) => ({ a: i.sha })`],
    [
      'shorthand properties of a destructured root',
      `({ input, build }) => ({ input, build })`,
    ],
    [
      'free identifiers (module constants)',
      `from => ({ release: RELEASE, url: CONFIG.url })`,
    ],
    [
      'comments and quoted keys',
      `from => ({ /* c */ "a-b": from.build, // x\n 2: from.input })`,
    ],
    [
      'strings containing operators',
      `from => ({ a: "x ? y : z && !w || v ?? u === t", b: from.input.sha })`,
    ],
    ['no parameters', `() => ({ a: 1 })`],
    // As Bun prints real functions: exponents, and template literals for awkward strings.
    ['a large number', String((from: any) => ({ n: 100000, m: -100000, a: from.build }))],
    ['a string with a newline', String(() => ({ s: 'a\nb' }))],
    ['a string with every quote kind', String(() => ({ s: `a"b'c\`d` }))],
    [
      'a template literal without substitutions',
      'from => ({ s: `plain`, a: from.build })',
    ],
  ];
  test.each(rows)('%s', (_, src) => expect(rejection(src)).toBeUndefined());
});

describe('rejected constructs', () => {
  const KEYWORDS = [
    'typeof',
    'void',
    'delete',
    'new',
    'await',
    'yield',
    'this',
    'super',
    'class',
    'function',
    'async',
    'if',
    'else',
    'for',
    'while',
    'do',
    'switch',
    'case',
    'try',
    'catch',
    'throw',
    'var',
    'let',
    'const',
    'in',
    'instanceof',
    'import',
    'export',
    'return',
    'arguments',
    'eval',
  ];
  const operators: [string, string][] = [
    ['?', 'a ternary (`? :`)'],
    ['??', 'nullish coalescing (`??`)'],
    ...(['&&', '||'] as const).map((o): [string, string] => [
      o,
      `a logical operator (\`${o}\`)`,
    ]),
    ['!', 'negation (`!`)'],
    ...['===', '!==', '==', '!=', '<', '>', '<=', '>='].map((o): [string, string] => [
      o,
      `a comparison (\`${o}\`)`,
    ]),
    ...['+', '-', '*', '/', '%', '**'].map((o): [string, string] => [
      o,
      `arithmetic/concatenation (\`${o}\`)`,
    ]),
    ...['=', '+=', '++', '--'].map((o): [string, string] => [
      o,
      `an assignment (\`${o}\`)`,
    ]),
    ...['&', '|', '^', '~', '@', '#'].map((o): [string, string] => [
      o,
      `the operator \`${o}\``,
    ]),
  ];
  const rows: [string, string, string][] = [
    ...operators.map(([o, what]): [string, string, string] => [
      `operator ${o}`,
      `from => ({ a: from.input.n ${o} 1 })`,
      op(what),
    ]),
    [
      'a ternary hidden after a number',
      `from => ({ n: 1, a: from.input.f ? from.build : from.test, m: 2 })`,
      op('a ternary (`? :`)'),
    ],
    [
      'unary minus on a reference',
      `from => ({ a: -from.input.n })`,
      op('arithmetic/concatenation (`-`)'),
    ],
    ...KEYWORDS.map((kw): [string, string, string] => [
      `keyword ${kw}`,
      `from => ({ a: ${kw}.x })`,
      `mapper uses \`${kw}\`; ${LITERALS_ONLY}`,
    ]),
    [
      'a substituting template string',
      'from => ({ a: `v${from.input.sha}` })',
      'mapper uses a template string',
    ],
    [
      'a method call',
      `from => ({ a: from.input.sha.slice(0, 7) })`,
      `mapper calls a function or method; ${LITERALS_ONLY}`,
    ],
    [
      'spread',
      `from => ({ ...from.build })`,
      'mapper uses spread (`...`); list the fields explicitly',
    ],
    [
      'optional chaining',
      `from => ({ a: from.deploy?.url })`,
      'mapper uses optional chaining (`?.`); declare the input optional(...) and map the whole ref instead',
    ],
    [
      'a computed key',
      `from => ({ [k]: from.build })`,
      'mapper uses a computed key; only literal keys are allowed',
    ],
    [
      'a computed access',
      `from => ({ a: from[k] })`,
      'mapper uses a computed property access; only literal keys (from["step-name"]) are allowed',
    ],
    [
      'a method',
      `from => ({ a() { return 1 } })`,
      `mapper defines a method; ${LITERALS_ONLY}`,
    ],
    ['async', `async from => ({ a: from.build })`, 'mapper must not be async'],
    [
      'a generator',
      `function* (from) { return { a: 1 } }`,
      'mapper must not be a generator',
    ],
    [
      'undefined',
      `from => ({ a: undefined })`,
      'mapper uses `undefined`; leave optional fields out instead',
    ],
    [
      'a default parameter',
      `(from = {}) => ({ a: 1 })`,
      'mapper parameter has a default value',
    ],
    [
      'a destructuring default',
      `({ input = 1 }) => ({ a: 1 })`,
      'mapper parameter destructuring has a default value',
    ],
    [
      'a declaration before return',
      `from => { const x = 1; return { a: x } }`,
      `mapper uses \`const\`; ${LITERALS_ONLY}`,
    ],
    [
      'a statement before return',
      `from => { x; return { a: 1 } }`,
      notObject(
        "unexpected 'x' (a block-bodied mapper must consist of a single `return { … }`)",
      ),
    ],
    [
      'a comma expression',
      `from => (n++, { a: from.build })`,
      notObject("unexpected 'n' (expected {)"),
    ],
    [
      'a non-object body',
      `from => from.build`,
      notObject(
        "unexpected 'from' (the mapper body must be an object literal: from => ({ … }))",
      ),
    ],
    [
      'an array parameter',
      `([a]) => ({ a: 1 })`,
      notObject("unexpected '[' (expected an identifier)"),
    ],
    [
      'an array hole',
      `from => ({ a: [1, , 2] })`,
      notObject("unexpected ',' (array holes are not allowed)"),
    ],
    [
      'anything after the body',
      `from => ({ f: from.input.flag }).f ? {a: from.build} : {a: from.test}`,
      notObject("unexpected '.' after the mapper body"),
    ],
    [
      'an unexpected character',
      `from => ({ a: from.input.a\\ })`,
      "mapper source has an unexpected character '\\'",
    ],
    [
      'an unterminated comment',
      `from => ({ a: 1 }) /* x`,
      'mapper source has an unterminated comment',
    ],
    [
      'an unterminated string',
      `from => ({ a: "x })`,
      'mapper source has an unterminated string',
    ],
  ];
  test.each(rows)('%s', (_, src, message) => expect(rejection(src)).toBe(message));
});
