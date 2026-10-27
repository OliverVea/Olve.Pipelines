import { closeSync, openSync, readSync } from 'node:fs';

export const GLIBC_INTERP = '/lib64/ld-linux-x86-64.so.2';
export const MUSL_INTERP = '/lib/ld-musl-x86-64.so.1';

function read(fd: number, pos: number, len: number): Buffer {
  const b = Buffer.alloc(len);
  const n = readSync(fd, b, 0, len, pos);
  if (n !== len) throw new Error('truncated ELF file');
  return b;
}

/** PT_INTERP of an x86-64 ELF executable, or null if it has none (static). */
export function readInterpreter(path: string): string | null {
  const fd = openSync(path, 'r');
  try {
    const h = read(fd, 0, 64);
    if (h.readUInt32BE(0) !== 0x7f454c46) throw new Error(`${path} is not an ELF file`);
    if (h[4] !== 2 || h[5] !== 1)
      throw new Error(`${path} is not a 64-bit little-endian ELF file`);
    const phoff = Number(h.readBigUInt64LE(0x20));
    const phentsize = h.readUInt16LE(0x36);
    const phnum = h.readUInt16LE(0x38);
    for (let i = 0; i < phnum; i++) {
      const ph = read(fd, phoff + i * phentsize, phentsize);
      if (ph.readUInt32LE(0) === 3 /* PT_INTERP */) {
        const off = Number(ph.readBigUInt64LE(0x08));
        const size = Number(ph.readBigUInt64LE(0x20));
        return read(fd, off, size).toString('latin1').replace(/\0+$/, '');
      }
    }
    return null;
  } finally {
    closeSync(fd);
  }
}
