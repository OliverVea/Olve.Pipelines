---
name: manual-test
description: Run the app locally and make authenticated API calls against the beta environment for manual testing.
allowed-tools: Bash Read Grep
---

# Manual Testing

## 1. Start the app locally

```bash
dotnet run --project src/Olve.Pipelines
# Listens on http://localhost:5000
```

## 2. Get a bearer token

The app requires JWT authentication from Authentik. Get a token using client_credentials grant with the OIDC client from user-secrets:

```bash
# Read credentials from user-secrets
dotnet user-secrets list --project src/Olve.Pipelines
# Use Storage:ClientId and Storage:ClientSecret

# Get token
TOKEN=$(curl -sk -X POST "https://auth-beta.ovea.pro/application/o/token/" \
  -d "grant_type=client_credentials" \
  -d "client_id=<Storage:ClientId>" \
  -d "client_secret=<Storage:ClientSecret>" \
  -d "scope=openid" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")
```

## 3. Make API calls

```bash
curl -s http://localhost:5000/api/pipelines \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json"
```

## 4. Monitor K8s jobs

Jobs execute on the remote K8s cluster via SSH:

```bash
ssh oliver@bulwark-m2 "kubectl get pods -n olve-runners-beta"
ssh oliver@bulwark-m2 "kubectl get jobs -n olve-runners-beta"
ssh oliver@bulwark-m2 "kubectl logs <pod-name> -n olve-runners-beta"
```

## Notes

- The app connects to MinIO at `https://minio-beta.ovea.pro` and K8s via OpenBao at `https://openbao-beta.ovea.pro`
- Tokens expire after 1 hour — re-run the token command if you get 401s
- The `olve-runners-beta` namespace is where K8s jobs are created
