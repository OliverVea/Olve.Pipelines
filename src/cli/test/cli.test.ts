import { describe, expect, test } from 'bun:test';
import { globalsLast, parseArgs, unknownOptions } from '../src/args';
import { fakeFetch, pl } from './helpers';

describe('arguments', () => {
  const spec = {
    flags: new Set(['json', 'force']),
    options: new Set(['field', 'profile']),
    aliases: { f: 'field' },
  };

  test('options repeat, flags stand alone, --name=value works', () => {
    const args = parseArgs(
      ['run', 'x', '--field', 'a=1', '--field=b=2', '--json', '-f', 'c=3'],
      spec,
    );
    expect(args.positionals).toEqual(['run', 'x']);
    expect(args.options('field')).toEqual(['a=1', 'b=2', 'c=3']);
    expect(args.option('field')).toBe('c=3');
    expect(args.flag('json')).toBe(true);
  });

  test('a flag with a value and an option without one are usage errors', () => {
    expect(() => parseArgs(['--json=1'], spec)).toThrow(
      "Flag '--json' does not take a value.",
    );
    expect(() => parseArgs(['--field'], spec)).toThrow(
      "Option '--field' requires a value.",
    );
  });

  test('leading globals move behind the command words', () => {
    expect(globalsLast(['--profile', 'beta', '--json', 'pipeline', 'list'])).toEqual([
      'pipeline',
      'list',
      '--profile',
      'beta',
      '--json',
    ]);
    expect(globalsLast(['--help'])).toEqual(['--help']);
    expect(globalsLast(['pipeline', 'list'])).toEqual(['pipeline', 'list']);
  });

  test('an unknown option is reported with the closest known one', () => {
    expect(unknownOptions(['--feild', 'a=1'], 'trigger', spec)).toEqual([
      "Unknown option '--feild' for `pl trigger` (did you mean '--field'?). Run `pl trigger --help`.",
    ]);
    expect(unknownOptions(['--field', 'a=1', '--json'], 'trigger', spec)).toEqual([]);
  });
});

describe('runner', () => {
  test('no command prints usage and exits 2; --version prints the stamp', async () => {
    const none = await pl([]);
    expect(none.code).toBe(2);
    expect(none.stdout).toContain('Usage:');
    const version = await pl(['--version']);
    expect(version.code).toBe(0);
    expect(version.stdout).toMatch(/^pl \S+\n$/);
  });

  test('help: `pl help`, `pl <noun> --help`, `pl <noun> <verb> --help`', async () => {
    expect((await pl(['help'])).stdout).toContain('pipeline list');
    expect((await pl(['pipeline', '--help'])).stdout).toContain(
      'pl pipeline — commands:',
    );
    expect((await pl(['pipeline', 'switch', '--help'])).stdout).toContain(
      '--cancel-running',
    );
  });

  test('unknown commands and missing arguments are usage errors', async () => {
    const unknown = await pl(['pipelne']);
    expect(unknown.code).toBe(2);
    expect(unknown.stderr).toContain("Unknown command 'pipelne'");
    const missing = await pl(['pipeline', 'get']);
    expect(missing.code).toBe(2);
    expect(missing.stderr).toContain('`pl pipeline get` expects 1 argument(s)');
    const bad = await pl(['pipeline', 'list', '--summarie']);
    expect(bad.code).toBe(2);
    expect(bad.stderr).toContain("did you mean '--summaries'");
  });

  test('server problems become error lines with their field, exit 1; 401 points at pl login', async () => {
    const { fetch } = fakeFetch(() => ({
      status: 400,
      body: {
        status: 400,
        code: 'invalid',
        errors: [{ message: 'is required', field: 'input.sha' }, { message: 'boom' }],
      },
    }));
    const failed = await pl(['pipeline', 'list'], { fetch });
    expect(failed.code).toBe(1);
    expect(failed.stderr).toBe('error: input.sha: is required\nerror: boom');
    const { fetch: unauthorized } = fakeFetch(() => ({ status: 401 }));
    expect((await pl(['pipeline', 'list'], { fetch: unauthorized })).stderr).toContain(
      'Run `pl login`',
    );
  });

  test('--token and PIPELINES_API_URL decide the request; --verbose logs it', async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: [] }));
    const result = await pl(['pipeline', 'list', '--token', 't0k', '--verbose'], {
      fetch,
      env: { PIPELINES_API_URL: 'https://beta.example/' },
    });
    expect(result.code).toBe(0);
    expect(requests[0]!.url).toBe('https://beta.example/api/pipelines');
    expect(requests[0]!.headers.get('Authorization')).toBe('Bearer t0k');
    expect(result.stderr).toContain(
      'api: https://beta.example/ (from PIPELINES_API_URL); token: --token',
    );
  });
});
