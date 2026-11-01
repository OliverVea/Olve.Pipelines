import { describe, expect, test } from 'bun:test';
import { join } from 'node:path';
import { discover } from '../src/auth/oidc';

const recording = join(import.meta.dir, '../../../tools/recordings/dex/discovery.json');

describe('discover', () => {
  test('reads the endpoints from what the pinned Dex serves', async () => {
    const { request, response } = await Bun.file(recording).json();
    const requested: string[] = [];
    const fetchFn = (async (url: string) => {
      requested.push(url);
      return Response.json(response.body, { status: response.status });
    }) as unknown as typeof fetch;

    const discovery = await discover(fetchFn, 'http://localhost:5556/dex/');

    expect(requested).toEqual([`http://localhost:5556${request.url}`]);
    expect(discovery).toEqual({
      authorizationEndpoint: 'http://localhost:5556/dex/auth',
      tokenEndpoint: 'http://localhost:5556/dex/token',
      deviceAuthorizationEndpoint: 'http://localhost:5556/dex/device/code',
    });
  });
});
