/** What a command prints: the API payload as JSON (`--json`), or the human rendering. */
export class Output {
  constructor(
    readonly json: boolean,
    private readonly stdout: (text: string) => void,
  ) {}

  /** `--json`: the value, pretty-printed; otherwise `human()` prints the lines. */
  emit(value: unknown, human: () => void): void {
    if (this.json) this.stdout(JSON.stringify(value ?? null, null, 2) + '\n');
    else human();
  }

  line(text = ''): void {
    this.stdout(text + '\n');
  }

  write(text: string): void {
    this.stdout(text);
  }

  lines(lines: Iterable<string>): void {
    for (const line of lines) this.line(line);
  }
}

/** Left-aligned columns two spaces apart; the last column is not padded. Headers are printed as given. */
export function table(
  headers: readonly string[],
  rows: readonly (readonly string[])[],
): string[] {
  const widths = headers.map((h, c) =>
    Math.max(h.length, ...rows.map((r) => (r[c] ?? '').length)),
  );
  const format = (cells: readonly string[]) =>
    widths
      .map((w, c) =>
        c === widths.length - 1 ? (cells[c] ?? '') : (cells[c] ?? '').padEnd(w),
      )
      .join('  ')
      .trimEnd();
  return [format(headers), ...rows.map(format)];
}
