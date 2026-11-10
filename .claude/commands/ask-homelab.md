# Ask Homelab Claude

Query the Claude Code instance on `bulwark-m2` to look up homelab infrastructure details.

Usage: `/ask-homelab <question>`

The homelab repo at `~/projects/homelab` on `bulwark-m2` contains Kubernetes manifests, secret references
and other infrastructure configuration.

**Secrets stay out of the transcript.** Ask where a secret lives and which keys it has, never for its
value. The remote instance must never print a secret value: it lists keys only (`jq '.data | keys'`)
and compares values by sha256.

## Examples

- `/ask-homelab Which Secret and keys does the olve-pipelines release read in the apps namespace?`
- `/ask-homelab How is the ingress configured for the apps namespace?`

## Implementation

Pass the question on stdin through a quoted heredoc, so nothing in it is interpreted by either shell:

```bash
ssh oliver@bulwark-m2 '~/.local/bin/claude -p --max-turns 10 2>/dev/null' <<'QUESTION'
$ARGUMENTS

Look in ~/projects/homelab for the answer. Never print a secret value: list keys only, compare values by sha256.
QUESTION
```
