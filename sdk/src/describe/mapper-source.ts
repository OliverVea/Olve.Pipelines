// Static analysis of mapper source (`fn.toString()`), an allowlist parser.
//
// Recording proxies cannot observe truthiness, `!`, `??`, `||`, `&&`, ternaries or `===` against a
// value: they silently record a constant. So describe also checks the mapper's *source*: its body
// must be an object literal whose values are member chains, literals, constant arrays or nested
// object literals. Everything else is rejected with the offending construct named.
//
// Accepted shapes (after Bun's TypeScript transpilation):
//   from => ({ a: from.input.sha, b: from["deploy-beta"].url, c: "lit", d: 1, e: { f: from.build } })
//   (from) => { return { … }; }
//   ({ input, "deploy-beta": beta }) => ({ sha: input.sha, url: beta.url })
//   function (from) { return { … }; }
// Free identifiers (`RELEASE`, `CONFIG.url`) are allowed: they are module constants, recorded as
// `{const}` by the proxy. This requires the harness bundle not to be minified.
import { MapperError } from "./record.ts";

type Tok =
  | { t: "ident"; v: string; pos: number }
  | { t: "str"; v: string; pos: number }
  | { t: "num"; v: string; pos: number }
  | { t: "punc"; v: string; pos: number }
  | { t: "op"; v: string; pos: number }
  | { t: "eof"; v: ""; pos: number };

const PUNCS = ["...", "=>", "?.", "(", ")", "{", "}", "[", "]", ",", ":", ".", ";"];
const OPS = ["===", "!==", "**=", "??=", "&&=", "||=", "==", "!=", "<=", ">=", "&&", "||", "??", "++", "--", "**", "<<", ">>", "+=", "-=", "*=", "/=", "?", "!", "+", "-", "*", "/", "%", "<", ">", "=", "&", "|", "^", "~", "@", "#"];

function describeOp(op: string): string {
  if (op === "?") return "a ternary (`? :`)";
  if (op === "??") return "nullish coalescing (`??`)";
  if (op === "&&" || op === "||") return `a logical operator (\`${op}\`)`;
  if (op === "!") return "negation (`!`)";
  if (["===", "!==", "==", "!=", "<", ">", "<=", ">="].includes(op)) return `a comparison (\`${op}\`)`;
  if (["+", "-", "*", "/", "%", "**"].includes(op)) return `arithmetic/concatenation (\`${op}\`)`;
  if (op.endsWith("=") || op === "++" || op === "--") return `an assignment (\`${op}\`)`;
  return `the operator \`${op}\``;
}

const KEYWORDS = new Set([
  "typeof", "void", "delete", "new", "await", "yield", "this", "super", "class", "function", "async",
  "if", "else", "for", "while", "do", "switch", "case", "try", "catch", "throw", "var", "let", "const",
  "in", "instanceof", "import", "export", "return", "arguments", "eval",
]);

function tokenize(src: string): Tok[] {
  const toks: Tok[] = [];
  let i = 0;
  while (i < src.length) {
    const c = src[i]!;
    if (/\s/.test(c)) { i++; continue; }
    if (src.startsWith("//", i)) { const e = src.indexOf("\n", i); i = e < 0 ? src.length : e + 1; continue; }
    if (src.startsWith("/*", i)) {
      const e = src.indexOf("*/", i + 2);
      if (e < 0) throw new MapperError("mapper source has an unterminated comment");
      i = e + 2;
      continue;
    }
    if (c === "`") throw new MapperError("mapper uses a template string");
    if (c === '"' || c === "'") {
      let j = i + 1;
      let v = "";
      for (;;) {
        if (j >= src.length) throw new MapperError("mapper source has an unterminated string");
        const d = src[j]!;
        if (d === c) break;
        if (d === "\\") { v += src.slice(j, j + 2); j += 2; continue; }
        v += d;
        j++;
      }
      toks.push({ t: "str", v, pos: i });
      i = j + 1;
      continue;
    }
    const num = /^(?:0[xXoObB][0-9a-fA-F_]+|(?:\d[\d_]*)?\.?\d[\d_]*(?:[eE][+-]?\d+)?)n?/.exec(src.slice(i));
    if (num && /[\d.]/.test(c) && num[0] !== ".") {
      toks.push({ t: "num", v: num[0], pos: i });
      i += num[0].length;
      continue;
    }
    const id = /^[\p{ID_Start}$_][\p{ID_Continue}$‌‍]*/u.exec(src.slice(i));
    if (id) {
      toks.push({ t: "ident", v: id[0], pos: i });
      i += id[0].length;
      continue;
    }
    const p = PUNCS.find((x) => src.startsWith(x, i));
    if (p && !(p === "?." && /\d/.test(src[i + 2] ?? ""))) {
      toks.push({ t: "punc", v: p, pos: i });
      i += p.length;
      continue;
    }
    const o = OPS.find((x) => src.startsWith(x, i));
    if (o) {
      toks.push({ t: "op", v: o, pos: i });
      i += o.length;
      continue;
    }
    throw new MapperError(`mapper source has an unexpected character '${c}'`);
  }
  toks.push({ t: "eof", v: "", pos: src.length });
  return toks;
}

/** Throws MapperError unless `src` is an allowed mapper. */
export function analyzeMapperSource(src: string): void {
  const toks = tokenize(src);
  let k = 0;
  const peek = (o = 0) => toks[Math.min(k + o, toks.length - 1)]!;
  const next = () => toks[k++]!;
  const is = (t: Tok["t"], v?: string, o = 0) => peek(o).t === t && (v === undefined || peek(o).v === v);
  const unexpected = (what = "here"): never => {
    const tok = peek();
    if (tok.t === "op") throw new MapperError(`mapper uses ${describeOp(tok.v)}; mappers may only contain property access and literals — move logic into the handler or a when block`);
    if (tok.t === "punc" && tok.v === "?.") throw new MapperError("mapper uses optional chaining (`?.`); declare the input optional(...) and map the whole ref instead");
    if (tok.t === "punc" && tok.v === "...") throw new MapperError("mapper uses spread (`...`); list the fields explicitly");
    if (tok.t === "punc" && tok.v === "(") throw new MapperError("mapper calls a function or method; mappers may only contain property access and literals");
    if (tok.t === "ident" && KEYWORDS.has(tok.v)) throw new MapperError(`mapper uses \`${tok.v}\`; mappers may only contain property access and literals`);
    throw new MapperError(`mapper source is not an object literal of property accesses and literals (unexpected ${tok.t === "eof" ? "end" : `'${tok.v}'`} ${what})`);
  };
  const expect = (t: Tok["t"], v?: string) => (is(t, v) ? next() : unexpected(`(expected ${v ?? t})`));

  const identifier = () => {
    if (!is("ident") || KEYWORDS.has(peek().v)) unexpected("(expected an identifier)");
    return next();
  };

  // ── parameters ──
  const params = () => {
    if (is("ident")) { identifier(); return; }
    expect("punc", "(");
    if (is("punc", ")")) { next(); return; }
    if (is("punc", "{")) destructuring();
    else identifier();
    if (is("punc", ",")) next();
    if (is("op", "=")) throw new MapperError("mapper parameter has a default value");
    expect("punc", ")");
  };
  const destructuring = () => {
    expect("punc", "{");
    while (!is("punc", "}")) {
      if (is("str")) { next(); expect("punc", ":"); identifier(); }
      else {
        identifier();
        if (is("punc", ":")) { next(); identifier(); }
      }
      if (is("op", "=")) throw new MapperError("mapper parameter destructuring has a default value");
      if (!is("punc", ",")) break;
      next();
    }
    expect("punc", "}");
  };

  // ── values ──
  const value = (): void => {
    if (is("punc", "(")) { next(); value(); expect("punc", ")"); return; }
    if (is("punc", "{")) return object();
    if (is("punc", "[")) return array();
    if (is("str") || is("num")) { next(); return; }
    if (is("op", "-") && is("num", undefined, 1)) { next(); next(); return; }
    if (is("ident", "true") || is("ident", "false") || is("ident", "null")) { next(); return; }
    if (is("ident", "undefined")) throw new MapperError("mapper uses `undefined`; leave optional fields out instead");
    chain();
  };
  const chain = () => {
    identifier();
    for (;;) {
      if (is("punc", ".")) { next(); if (!is("ident")) unexpected("(expected a property name)"); next(); continue; }
      if (is("punc", "[")) {
        next();
        if (!is("str") && !is("num")) throw new MapperError("mapper uses a computed property access; only literal keys (from[\"step-name\"]) are allowed");
        next();
        expect("punc", "]");
        continue;
      }
      break;
    }
  };
  const array = () => {
    expect("punc", "[");
    while (!is("punc", "]")) {
      if (is("punc", ",")) unexpected("(array holes are not allowed)");
      value();
      if (!is("punc", ",")) break;
      next();
    }
    expect("punc", "]");
  };
  const object = () => {
    expect("punc", "{");
    while (!is("punc", "}")) {
      if (is("punc", "...")) unexpected();
      if (is("punc", "[")) throw new MapperError("mapper uses a computed key; only literal keys are allowed");
      if (is("ident") && (is("punc", ",", 1) || is("punc", "}", 1))) {
        // shorthand `{ input }` — a destructured root or a module constant
        identifier();
      } else {
        if (is("ident") || is("str") || is("num")) next();
        else unexpected("(expected a property name)");
        if (is("punc", "(")) throw new MapperError("mapper defines a method; mappers may only contain property access and literals");
        expect("punc", ":");
        value();
      }
      if (!is("punc", ",")) break;
      next();
    }
    expect("punc", "}");
  };
  const returnBody = () => {
    expect("punc", "{");
    if (!is("ident", "return")) unexpected("(a block-bodied mapper must consist of a single `return { … }`)");
    next();
    object();
    if (is("punc", ";")) next();
    expect("punc", "}");
  };

  // ── function ──
  if (is("ident", "async")) throw new MapperError("mapper must not be async");
  if (is("ident", "function")) {
    next();
    if (is("op", "*")) throw new MapperError("mapper must not be a generator");
    if (is("ident")) next();
    params();
    returnBody();
  } else {
    params();
    expect("punc", "=>");
    if (is("punc", "(")) {
      next();
      object();
      expect("punc", ")");
    } else if (is("punc", "{")) {
      returnBody();
    } else unexpected("(the mapper body must be an object literal: from => ({ … }))");
  }
  if (is("punc", ";")) next();
  if (!is("eof")) unexpected("after the mapper body");
}
