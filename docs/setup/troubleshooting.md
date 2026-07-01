# Troubleshooting

[← Index](index.md) · [Subject Index](subjects.md)

Symptoms → likely cause → fix. Most setup problems surface in
`pl binding status <pipelineId>` as reconcile `Error` with a list of problems — start there.

## The reconcile shows `Error`

Live state is **unchanged** (the [config-before-build](binding-and-reconcile.md#config-before-build)
guarantee). Read the listed problems and match the message:

| Problem message contains | Cause | Fix |
|---|---|---|
| `Config file 'config.yaml' not found` | No `config.yaml` under the bound `path` | Add `.pipelines/config.yaml`, or fix the binding `path` |
| `must be a YAML mapping` | Top-level YAML isn't a key/value map | The file must start with `apiVersion:` etc., not a list/scalar |
| `Failed to parse 'config.yaml'` | Invalid YAML | Fix the YAML syntax in the named file |
| `Config 'name' is required` | Missing/empty `name` | Set `name:` |
| `ApiVersion '…' is incompatible` | Major doesn't match the server | Use `apiVersion: "0.0"` (current major is 0) |
| `ApiVersion '…' is not in 'major.minor' form` | Bad version string | Use `"0.0"`, not `"0"` or `"v0"` |
| `Duplicate production step name` / `Duplicate processing step name` | Two steps share a name | Make step names unique within each list |
| `references unknown processing step` | A `processing` trigger names a step that doesn't exist | Fix `processingStepName` to match a `processingSteps[].name` |
| `is referenced but not declared in 'secrets:'` | `$SECRET:NAME` used but not declared | Add the secret to `secrets:` |
| `Step '$ref: …' must not set any other fields` | A `$ref` step has extra keys | A `$ref` step must contain *only* `$ref` |
| `Referenced step file '…' not found` | `$ref` points at a missing file | Add the file under the config dir, or fix the path |
| `Script file '…' not found` | `scriptFile` points at a missing file | Add the script, or fix the path |
| `sets both 'script' and 'scriptFile'` | Both inline and file script set | Use exactly one |

After fixing, push again — the next poll cycle re-reconciles.

## A secret shows `unset`

The secret is declared in `config.yaml` but its value isn't set in k8s. Set it (the value is
read from stdin, so it stays out of your shell history):

```sh
echo -n "<value>" | pl secret set <pipelineId> <name>
```

This is operational, so it works on a bound pipeline.

## A secret shows `unknown`

`unknown` ≠ `unset`. It means the server **couldn't read** the k8s secret (k8s unconfigured or
unreachable at read time) — the set/unset state is genuinely unknown. Check k8s connectivity;
`pl binding status` deliberately won't fail or report a misleading "unset" in this case.

## I want to change steps or triggers without pushing

You can't, by design — there is no `pl` command (and no API) that mutates pipeline shape.
Creating/deleting/reordering steps, setting step configuration, and creating/deleting triggers
all come **only** from `config.yaml` via reconcile — your repo is the only config writer. Change
the file and push instead. Operational commands (`pl production trigger`, `pl job cancel`,
`pl secret set`, the promotion gate) work on a bound pipeline. See
[git-only](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints).

## I pushed but nothing happened

- The poll runs about every **5 minutes** — give it a cycle.
- A **code-only** push (nothing under `.pipelines/` changed) still builds but skips the
  reconcile; if you expected a *shape* change, confirm you actually edited `.pipelines/`.
- Check the reconcile result / last-sync time via `pl binding status <pipelineId>` to see
  whether a reconcile ran. Force one now with `pl binding reconcile <pipelineId>`.

## The build runs but the deploy never starts

- A processing step's **promotion gate** may be braked. Check/unblock it — see
  [Promotion Gate](promotion-gate.md). A braked gate halts the chain *without* skipping ahead.
- An earlier processing step may have **failed**: processing is sequential, so a failure stops
  the chain. Inspect the failing job: `pl job list --pipeline <pipelineId>` to find it, then
  `pl job logs <jobId>` for the error.

## A new job seems to have replaced my queued one

That's **latest-wins** scheduling. A new job for the same `(pipeline, step)` key marks older
`Scheduled` jobs `Obsolete`; only the newest runs. This is by design — see
[scheduling rules](bundles-and-execution.md#jobs-and-scheduling).

## Private repo: the fetch fails / branch head 4xx

The repo read token is resolved at fetch time from the pipeline's k8s secret using the
`credentialsSecret` **key name** you bound. Confirm:

- `credentialsSecret` names a key that actually exists in the secret (e.g. `GITHUB_TOKEN`).
- That key's **value** is set (`pl secret set <pipelineId> <name>`) and is a valid read token
  for the repo. Confirm with `pl secret list <pipelineId>` or `pl binding status <pipelineId>`.

A public repo needs no token — omit `credentialsSecret`.

## See also

- [Binding & Reconcile](binding-and-reconcile.md) — the loop and the status fields
- [`config.yaml` Reference](config-reference.md#validation-rules-reconcile-rejects-on-any-of-these) — the full validation table
