// Deterministic tar writer (ustar + PAX long names), streaming from disk. Entries are sorted,
// with mtime 0, uid/gid 0, no user names — so equal content gives byte-identical archives (and
// sha256s). Extraction uses Bun.Archive.
import {
  closeSync,
  createReadStream,
  createWriteStream,
  lstatSync,
  openSync,
  readdirSync,
  readlinkSync,
  readSync,
  renameSync,
  rmSync,
  writeSync,
} from 'node:fs';
import { join } from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createGzip } from 'node:zlib';

export type TarEntry =
  | { path: string; type: 'file'; mode: number; source: string }
  | { path: string; type: 'bytes'; mode: number; data: Uint8Array }
  | { path: string; type: 'dir'; mode: number }
  | { path: string; type: 'symlink'; target: string };

const enc = new TextEncoder();

function octal(n: number, len: number): string {
  return n.toString(8).padStart(len - 1, '0') + '\0';
}

function header(
  name: string,
  size: number,
  mode: number,
  typeflag: string,
  linkname = '',
): Uint8Array {
  const h = new Uint8Array(512);
  const put = (s: string | Uint8Array, off: number, len: number) => {
    const b = typeof s === 'string' ? enc.encode(s) : s;
    h.set(b.subarray(0, len), off);
  };
  put(name, 0, 100);
  put(octal(mode & 0o7777, 8), 100, 8);
  put(octal(0, 8), 108, 8);
  put(octal(0, 8), 116, 8);
  put(octal(size, 12), 124, 12);
  put(octal(0, 12), 136, 12);
  put('        ', 148, 8); // checksum placeholder
  put(typeflag, 156, 1);
  put(linkname, 157, 100);
  put('ustar\0', 257, 6);
  put('00', 263, 2);
  let sum = 0;
  for (const b of h) sum += b;
  put(octal(sum, 7) + ' ', 148, 8);
  return h;
}

function paxRecord(key: string, value: string): string {
  const body = ` ${key}=${value}\n`;
  let len = enc.encode(body).length;
  let total = len + String(len).length;
  if (String(total).length !== String(len).length) total = len + String(total).length;
  return `${total}${body}`;
}

/** Writes `entries` (sorted by path here) as an uncompressed tar to `out`. */
export function writeTar(out: string, entries: readonly TarEntry[]): void {
  const sorted = [...entries].sort((a, b) =>
    a.path < b.path ? -1 : a.path > b.path ? 1 : 0,
  );
  const tmp = `${out}.partial`;
  const fd = openSync(tmp, 'w');
  const write = (b: Uint8Array) => {
    let off = 0;
    while (off < b.length) off += writeSync(fd, b, off, b.length - off);
  };
  const pad = (size: number) => {
    const r = size % 512;
    if (r) write(new Uint8Array(512 - r));
  };
  try {
    for (const e of sorted) {
      const name = e.type === 'dir' && !e.path.endsWith('/') ? e.path + '/' : e.path;
      const link = e.type === 'symlink' ? e.target : '';
      let size = 0;
      if (e.type === 'file') size = lstatSync(e.source).size;
      else if (e.type === 'bytes') size = e.data.length;
      const pax: string[] = [];
      if (enc.encode(name).length > 100) pax.push(paxRecord('path', name));
      if (enc.encode(link).length > 100) pax.push(paxRecord('linkpath', link));
      if (pax.length) {
        const body = enc.encode(pax.join(''));
        write(header('././@PaxHeader', body.length, 0o644, 'x'));
        write(body);
        pad(body.length);
      }
      const shortName = enc.encode(name).length > 100 ? name.slice(0, 99) : name;
      const mode = e.type === 'symlink' ? 0o777 : e.mode;
      const flag = e.type === 'dir' ? '5' : e.type === 'symlink' ? '2' : '0';
      write(
        header(shortName, size, mode, flag, link.length > 100 ? link.slice(0, 99) : link),
      );
      if (e.type === 'bytes') {
        write(e.data);
        pad(size);
      } else if (e.type === 'file') {
        const src = openSync(e.source, 'r');
        try {
          const buf = new Uint8Array(1 << 20);
          let left = size;
          while (left > 0) {
            const n = readSync(src, buf, 0, Math.min(buf.length, left), null);
            if (n <= 0) throw new Error(`${e.source} shrank while archiving`);
            write(buf.subarray(0, n));
            left -= n;
          }
        } finally {
          closeSync(src);
        }
        pad(size);
      }
    }
    write(new Uint8Array(1024));
  } finally {
    closeSync(fd);
  }
  renameSync(tmp, out);
}

/** Entries for the contents of `dir` (recursively), with paths prefixed by `prefix` ("" = none). */
export function dirEntries(dir: string, prefix = ''): TarEntry[] {
  const out: TarEntry[] = [];
  const walk = (abs: string, rel: string) => {
    for (const name of readdirSync(abs).sort()) {
      const a = join(abs, name);
      const r = rel ? `${rel}/${name}` : name;
      const st = lstatSync(a);
      if (st.isSymbolicLink())
        out.push({ path: r, type: 'symlink', target: readlinkSync(a) });
      else if (st.isDirectory()) {
        out.push({ path: r, type: 'dir', mode: 0o755 });
        walk(a, r);
      } else if (st.isFile())
        out.push({
          path: r,
          type: 'file',
          mode: st.mode & 0o111 ? 0o755 : 0o644,
          source: a,
        });
      // sockets, fifos, devices are skipped
    }
  };
  walk(dir, prefix);
  return out;
}

/** gzip `src` into `out` (streaming; zlib writes mtime 0, so output is deterministic). */
export async function gzipFile(src: string, out: string, level = 6): Promise<void> {
  const tmp = `${out}.partial`;
  await pipeline(createReadStream(src), createGzip({ level }), createWriteStream(tmp));
  renameSync(tmp, out);
}

/** Writes a deterministic `.tar.gz`. */
export async function writeTarGz(
  out: string,
  entries: readonly TarEntry[],
  level = 6,
): Promise<void> {
  const tar = `${out}.tar-tmp`;
  try {
    writeTar(tar, entries);
    await gzipFile(tar, out, level);
  } finally {
    rmSync(tar, { force: true });
  }
}

/** Writes a deterministic zstd-compressed tar (level 3: fast to write, and unpacks far faster than gzip). */
export async function writeTarZst(
  out: string,
  entries: readonly TarEntry[],
  level = 3,
): Promise<void> {
  const tar = `${out}.tar-tmp`;
  const tmp = `${out}.partial`;
  try {
    writeTar(tar, entries);
    await Bun.write(tmp, await Bun.zstdCompress(await Bun.file(tar).bytes(), { level }));
    renameSync(tmp, out);
  } finally {
    rmSync(tar, { force: true });
  }
}

/** Extracts a tar or tar.gz file into `dir`. */
export async function extractTar(archive: string, dir: string): Promise<void> {
  const a = new Bun.Archive(await Bun.file(archive).bytes());
  await a.extract(dir);
}
