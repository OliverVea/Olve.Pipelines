// `import script from "./scripts/x.sh" with { type: "file" }` yields a path string
// (inside the compiled binary it points into the embedded /$bunfs filesystem).
declare module '*.sh' {
  const path: string;
  export default path;
}
declare module '*.bash' {
  const path: string;
  export default path;
}
