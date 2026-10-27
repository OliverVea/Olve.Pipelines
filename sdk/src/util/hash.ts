import { openSync, readSync, closeSync } from 'node:fs';

export function sha256Hex(data: string | Uint8Array | ArrayBuffer): string {
  return new Bun.CryptoHasher('sha256').update(data).digest('hex');
}

/** Streaming SHA-256 of a file (constant memory). */
export function sha256File(path: string): string {
  const h = new Bun.CryptoHasher('sha256');
  const fd = openSync(path, 'r');
  try {
    const buf = new Uint8Array(1 << 20);
    for (;;) {
      const n = readSync(fd, buf, 0, buf.length, null);
      if (n <= 0) break;
      h.update(buf.subarray(0, n));
    }
  } finally {
    closeSync(fd);
  }
  return h.digest('hex');
}
