/**
 * A QR code as terminal text: two modules per character cell with the upper half block (`▀`), its
 * foreground painting the top module and its background the bottom one. Colours are explicit
 * (black modules on a white quiet zone) so it scans on any terminal theme.
 */
import QRCode from 'qrcode';

const BORDER = 2; // light modules around the symbol (the quiet zone)
const ESC = '\x1b';
const RESET = `${ESC}[0m`;
const colours = {
  '11': `${ESC}[30;40m`, // dark over dark
  '10': `${ESC}[30;107m`, // dark over light
  '01': `${ESC}[97;40m`, // light over dark
  '00': `${ESC}[97;107m`, // light over light
} as const;

/** The QR code of `text`, or undefined if it can't be encoded. */
export function renderQr(text: string): string | undefined {
  let modules: { size: number; get(row: number, col: number): number };
  try {
    modules = QRCode.create(text, { errorCorrectionLevel: 'M' }).modules;
  } catch {
    return undefined;
  }
  const dark = (row: number, col: number) =>
    row >= 0 &&
    col >= 0 &&
    row < modules.size &&
    col < modules.size &&
    modules.get(row, col) === 1;

  const lines: string[] = [];
  for (let row = -BORDER; row < modules.size + BORDER; row += 2) {
    let line = '';
    for (let col = -BORDER; col < modules.size + BORDER; col++) {
      const key =
        `${dark(row, col) ? 1 : 0}${dark(row + 1, col) ? 1 : 0}` as keyof typeof colours;
      line += `${colours[key]}▀`;
    }
    lines.push(`${line}${RESET}`);
  }
  return lines.join('\n');
}
