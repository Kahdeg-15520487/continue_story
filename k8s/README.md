# Continue Story — Kubernetes deployment (ArgoCD GitOps)
#
# Applies via the `continue-story` ArgoCD Application
# (declared in k3s_lab → argocd/applications.yaml).

## Manual bootstrap (one-time, not in git)

```bash
# 1. GHCR image pull secret (same one spf-mmo uses):
kubectl get secret ghcr-auth -n spf-mmo -o yaml \
  | sed 's/namespace: spf-mmo/namespace: continue-story/' \
  | kubectl apply -f -

# 2. Optional LLM provider keys (the agent env currently mirrors local
#    docker-compose: no keys set):
kubectl create secret generic agent-keys -n continue-story \
  --from-literal=ANTHROPIC_API_KEY=... \
  --from-literal=OPENAI_API_KEY=... \
  --from-literal=DEEPSEEK_API_KEY=... \
  --from-literal=OPENROUTER_API_KEY=... \
  --from-literal=GOOGLE_API_KEY=... \
  --from-literal=OPENAI_BASE_URL=...
# then uncomment envFrom in k8s/agent.yaml
```

## Layout

| File | Resource |
|---|---|
| namespace.yaml | `continue-story` namespace |
| pvc.yaml | library-data (20Gi) + sqlite-data (2Gi), local-path RWO |
| api.yaml | API deployment + service (port 5000) |
| agent.yaml | Agent deployment + service (port 3001) |
| frontend.yaml | Frontend node server + service (port 3000) |
| searxng.yaml | SearXNG + service (port 8080, internal) |
| configmap-searxng.yaml | /etc/searxng/settings.yml |
| configmap-skills.yaml | /skills/lore-extraction (mirrors compose mount) |
| middleware.yaml | Authelia forward-auth + strip-auth |
| ingressroute.yaml | cs.minhnguyenle.work → /api → api, /* → frontend |

## Constraints

- **api + agent are nodePinned to `k3sagent01`** — they share the RWO
  `library-data` PVC (local-path has no RWX). To move them, use an RWX
  provider and drop `nodeSelector`.
- Production frontend image is built with `--target production`
  (`adapter-node`, port 3000). Rebuild + push before ArgoCD syncs:
  ```bash
  docker build --target production -t ghcr.io/kahdeg-15520487/story-engine-frontend:latest ./frontend
  docker push ghcr.io/kahdeg-15520487/story-engine-frontend:latest
  ```
