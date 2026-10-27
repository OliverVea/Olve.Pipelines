import { describe, expect, test } from 'bun:test';
import { analyzeMapperSource } from '../../src/describe/mapper-source.ts';
import { MapperError } from '../../src/describe/record.ts';

const ok = (src: string) => expect(() => analyzeMapperSource(src)).not.toThrow();
const bad = (src: string, msg: RegExp) => {
  expect(() => analyzeMapperSource(src)).toThrow(MapperError);
  expect(() => analyzeMapperSource(src)).toThrow(msg);
};

describe('accepted mapper shapes', () => {
  test('arrow with parenthesised object literal', () =>
    ok(`from => ({ repo: from.input.repo, sha: from.input.sha })`));
  test('parenthesised param, trailing comma', () => ok(`(from) => ({ a: from.build, })`));
  test('bracket access for hyphenated step names', () =>
    ok(`from => ({ url: from["deploy-beta"].url, b: from['x-y'] })`));
  test('literals, negative numbers, nested objects, constant arrays', () =>
    ok(
      `from => ({ s: "x", n: -1.5, t: true, f: false, z: null, o: { a: from.input.sha, b: [1, "two"] } })`,
    ));
  test('block body with a single return', () =>
    ok(`(from) => { return { a: from.build }; }`));
  test('function expression', () =>
    ok(`function (from) { return { a: from.input.sha } }`));
  test('destructured parameter', () =>
    ok(`({ input, "deploy-beta": beta }) => ({ sha: input.sha, url: beta.url, input })`));
  test('free identifiers (module constants)', () =>
    ok(`from => ({ release: RELEASE, url: CONFIG.url })`));
  test('comments and quoted keys', () =>
    ok(`from => ({ /* c */ "a-b": from.build, // x\n 2: from.input })`));
  test('strings containing operators', () =>
    ok(`from => ({ a: "x ? y : z && !w || v ?? u === t", b: from.input.sha })`));
  test('no parameters', () => ok(`() => ({ a: 1 })`));
});

describe('rejected constructs', () => {
  test('ternary', () =>
    bad(`from => ({ a: from.input.publishCli ? from.build : from.test })`, /ternary/));
  test('&& / ||', () => {
    bad(`from => ({ a: from.input.a && from.input.b })`, /logical/);
    bad(`from => ({ a: from.input.a || "d" })`, /logical/);
  });
  test('??', () => bad(`from => ({ a: from.input.a ?? "d" })`, /nullish/));
  test('negation', () => bad(`from => ({ a: !from.input.a })`, /negation/));
  test('comparison', () =>
    bad(`from => ({ a: from.input.deployTo === "prod" })`, /comparison/));
  test('arithmetic / concatenation', () =>
    bad(`from => ({ a: "v" + from.input.sha })`, /arithmetic/));
  test('template string', () => bad('from => ({ a: `v${from.input.sha}` })', /template/));
  test('method call', () =>
    bad(`from => ({ a: from.input.sha.slice(0, 7) })`, /calls a function/));
  test('spread', () => bad(`from => ({ ...from.build })`, /spread/));
  test('optional chaining', () =>
    bad(`from => ({ a: from.deploy?.url })`, /optional chaining/));
  test('computed key / access', () => {
    bad(`from => ({ [k]: from.build })`, /computed key/);
    bad(`from => ({ a: from[k] })`, /computed property/);
  });
  test('async', () => bad(`async from => ({ a: from.build })`, /async/));
  test('statements before return', () =>
    bad(`from => { const x = 1; return { a: x } }`, /`const`|single `return/));
  test('comma expressions / side effects', () =>
    bad(`from => (n++, { a: from.build })`, /object literal|assignment/));
  test('typeof / new', () => {
    bad(`from => ({ a: typeof from.build })`, /typeof/);
    bad(`from => ({ a: new Date() })`, /new/);
  });
  test('undefined', () => bad(`from => ({ a: undefined })`, /undefined/));
  test('non-object body', () => bad(`from => from.build`, /object literal/));
  test('default parameter', () => bad(`(from = {}) => ({ a: 1 })`, /default value/));
});
