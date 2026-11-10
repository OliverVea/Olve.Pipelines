import type { ExpiringSecret } from '@olve/api';

const DAY_MS = 24 * 60 * 60 * 1000;

const days = (n: number) => `${n} day${n === 1 ? '' : 's'}`;

/** "NPM_TOKEN expires in 9 days", "… expires today", "… expired 3 days ago". */
export function expiryText(s: ExpiringSecret, now: Date): string {
  const left = Math.floor((Date.parse(s.expiresAt) - now.getTime()) / DAY_MS);
  if (s.expired) {
    const ago = Math.max(0, -left - 1);
    return `${s.name} expired ${ago === 0 ? 'today' : `${days(ago)} ago`}`;
  }
  return `${s.name} expires ${left === 0 ? 'today' : `in ${days(left)}`}`;
}

/** The value of an `<input type="date">` as an expiry: midnight UTC of that day; empty is none. */
export function expiryFromDate(value: string): string | undefined {
  return value ? `${value}T00:00:00Z` : undefined;
}
