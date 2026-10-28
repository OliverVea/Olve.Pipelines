// Hey API client generation: ../../artifacts/spec/openapi.json → ../../artifacts/clients/ts (gitignored).
// baseUrl: false — no baked-in default; the frontend configures same-origin.
export default {
  input: "../../artifacts/spec/openapi.json",
  output: { path: "../../artifacts/clients/ts", tsConfigPath: null },
  plugins: [{ name: "@hey-api/client-fetch", baseUrl: false }],
};
