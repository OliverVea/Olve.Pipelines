# Ask Homelab Claude

Query the Claude Code instance on `bulwark-m2` to look up homelab infrastructure details.

Usage: `/ask-homelab <question>`

The homelab repo at `~/projects/homelab` on `bulwark-m2` contains Kubernetes manifests, secrets references, Authentik config, OpenBao policies, and other infrastructure configuration.

## Examples

- `/ask-homelab What are the OIDC client credentials for olve-pipelines?`
- `/ask-homelab What secrets are in the olve-pipelines-oidc Kubernetes secret in the apps namespace?`
- `/ask-homelab How is the ingress configured for the apps namespace?`

## Implementation

Run the following, substituting the user's question for `$QUESTION`:

```bash
ssh oliver@bulwark-m2 "~/.local/bin/claude -p '$QUESTION Look in ~/projects/homelab for the answer.' --max-turns 10 2>/dev/null"
```

If the question involves sensitive values (secrets, keys, tokens), the remote instance has access to the actual values in the homelab repo.

$ARGUMENTS
