import { describe, expect, test } from 'vitest';
import { expiryText } from '../src/model/secrets.js';

const now = new Date('2026-11-10T12:00:00Z');

describe('expiryText', () => {
  test.each([
    ['2026-11-20T00:00:00Z', false, 'NPM_TOKEN expires in 9 days'],
    ['2026-11-11T13:00:00Z', false, 'NPM_TOKEN expires in 1 day'],
    ['2026-11-10T18:00:00Z', false, 'NPM_TOKEN expires today'],
    ['2026-11-10T06:00:00Z', true, 'NPM_TOKEN expired today'],
    ['2026-11-07T06:00:00Z', true, 'NPM_TOKEN expired 3 days ago'],
  ])('expiring %s (expired %s) reads %p', (expiresAt, expired, text) =>
    expect(expiryText({ name: 'NPM_TOKEN', expiresAt, expired }, now)).toBe(text),
  );
});
