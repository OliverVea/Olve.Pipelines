// `import x from "./x.sh" with { type: "file" }` yields a path (in /$bunfs when compiled).
declare module "*.sh" {
  const path: string;
  export default path;
}
