/** Line diff (LCS) with folded unchanged context, for manifest diffs. */

export type DiffLine = { kind: 'same' | 'add' | 'del'; text: string; a?: number; b?: number };
export type DiffHunk = { lines: DiffLine[] } | { skipped: number };

export function diffLines(aText: string, bText: string): DiffLine[] {
  const a = aText.split('\n');
  const b = bText.split('\n');
  // Trim common prefix/suffix to keep the table small.
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }
  const n = endA - start;
  const m = endB - start;
  const table = new Uint32Array((n + 1) * (m + 1));
  const w = m + 1;
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      table[i * w + j] =
        a[start + i] === b[start + j]
          ? table[(i + 1) * w + j + 1] + 1
          : Math.max(table[(i + 1) * w + j], table[i * w + j + 1]);
    }
  }
  const out: DiffLine[] = [];
  for (let k = 0; k < start; k++) out.push({ kind: 'same', text: a[k], a: k + 1, b: k + 1 });
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[start + i] === b[start + j]) {
      out.push({ kind: 'same', text: a[start + i], a: start + i + 1, b: start + j + 1 });
      i++;
      j++;
    } else if (table[(i + 1) * w + j] >= table[i * w + j + 1]) {
      out.push({ kind: 'del', text: a[start + i], a: start + i + 1 });
      i++;
    } else {
      out.push({ kind: 'add', text: b[start + j], b: start + j + 1 });
      j++;
    }
  }
  while (i < n) {
    out.push({ kind: 'del', text: a[start + i], a: start + i + 1 });
    i++;
  }
  while (j < m) {
    out.push({ kind: 'add', text: b[start + j], b: start + j + 1 });
    j++;
  }
  for (let k = 0; k < a.length - endA; k++)
    out.push({ kind: 'same', text: a[endA + k], a: endA + k + 1, b: endB + k + 1 });
  return out;
}

/** Group a diff into hunks with `context` unchanged lines around each change. */
export function foldDiff(lines: DiffLine[], context = 3): DiffHunk[] {
  const keep = new Array<boolean>(lines.length).fill(false);
  lines.forEach((l, idx) => {
    if (l.kind !== 'same') {
      for (let k = Math.max(0, idx - context); k <= Math.min(lines.length - 1, idx + context); k++) keep[k] = true;
    }
  });
  const hunks: DiffHunk[] = [];
  let current: DiffLine[] = [];
  let skipped = 0;
  lines.forEach((l, idx) => {
    if (keep[idx]) {
      if (skipped) {
        hunks.push({ skipped });
        skipped = 0;
      }
      current.push(l);
    } else {
      if (current.length) {
        hunks.push({ lines: current });
        current = [];
      }
      skipped++;
    }
  });
  if (current.length) hunks.push({ lines: current });
  if (skipped) hunks.push({ skipped });
  return hunks;
}
