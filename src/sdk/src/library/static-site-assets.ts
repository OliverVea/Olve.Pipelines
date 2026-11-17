// What steps.staticSite builds and deploys: the image's Dockerfile and Caddyfile, and the Helm chart. Kept as
// text in TS so they reach the step in a compiled binary and in the published package alike.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

/** Caddy, pinned by digest; only its binary goes into the site's image. */
export const CADDY_IMAGE =
  'caddy:2.11.9@sha256:4e2425f6009577e7c651010f16679c28f7d12df3e7b1914f941a7a3370746b3c';

/** The port Caddy listens on in the image (non-root, so above 1024). */
export const SITE_PORT = 8080;

/** The image: the Caddy binary, its config and the site (`wwwroot/` of the context) on an empty image. */
export function siteDockerfile(): string {
  return `FROM ${CADDY_IMAGE} AS caddy

# A plain copy drops the binary's cap_net_bind_service file capability: it listens on ${SITE_PORT}.
RUN cp /usr/bin/caddy /caddy

FROM scratch
COPY --from=caddy /caddy /caddy
COPY Caddyfile /etc/caddy/Caddyfile
COPY wwwroot /srv
USER 65532:65532
EXPOSE ${SITE_PORT}
ENTRYPOINT ["/caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
`;
}

export interface CaddyfileOptions {
  /** Adds `'wasm-unsafe-eval'` to the CSP's `script-src`. */
  wasm: boolean;
  /** Unknown paths are answered with `/index.html`. */
  spa: boolean;
}

/** The Caddyfile: static files from /srv, precompressed variants, security headers and a CSP. */
export function siteCaddyfile(o: CaddyfileOptions): string {
  const scriptSrc = o.wasm ? "'self' 'wasm-unsafe-eval'" : "'self'";
  const csp = [
    "default-src 'self'",
    `script-src ${scriptSrc}`,
    "style-src 'self'",
    "img-src 'self' data:",
    "object-src 'none'",
    "base-uri 'none'",
    "frame-ancestors 'none'",
  ].join('; ');
  return `{
	admin off
	persist_config off
	auto_https off
	storage_check off
	storage_clean_interval off
	servers {
		protocols h1
	}
}

:${SITE_PORT} {
	root * /srv
${o.spa ? '\ttry_files {path} {path}/ /index.html\n' : ''}\tfile_server {
		precompressed br gzip
	}
	header {
		-Server
		X-Content-Type-Options nosniff
		Referrer-Policy no-referrer
		Cross-Origin-Opener-Policy same-origin
		Content-Security-Policy "${csp}"
	}
}
`;
}

/** The chart's name and files (path → content). */
export const SITE_CHART_NAME = 'static-site';

export const SITE_CHART: Readonly<Record<string, string>> = {
  'Chart.yaml': `apiVersion: v2
name: ${SITE_CHART_NAME}
description: A static site served by Caddy (steps.staticSite).
type: application
version: 1.0.0
`,
  'values.yaml': `image:
  repository: ""
  tag: ""
  digest: ""
  pullPolicy: IfNotPresent

service:
  port: 80

resources:
  requests:
    cpu: 10m
    memory: 32Mi
  limits:
    cpu: 200m
    memory: 128Mi

# Labels every resource olve-preview=<value> (a pull request's preview).
preview: ""

# An Ingress is rendered when host is set; tlsSecret adds TLS with that certificate Secret.
ingress:
  host: ""
  tlsSecret: ""
  className: ""
  annotations: {}

# Who may reach the pod: the ingress controller, and step pods (health checks, smoke tests).
networkPolicy:
  enabled: true
  from:
    - namespaceSelector:
        matchLabels:
          kubernetes.io/metadata.name: kube-system
      podSelector:
        matchLabels:
          app.kubernetes.io/name: traefik
    - namespaceSelector: {}
      podSelector:
        matchLabels:
          app: olve-pipelines
`,
  'templates/_helpers.tpl': `{{- define "static-site.labels" -}}
app: {{ .Release.Name }}
{{- with .Values.preview }}
olve-preview: {{ . | quote }}
{{- end }}
{{- end }}
`,
  'templates/deployment.yaml': `apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}
  labels:
    {{- include "static-site.labels" . | nindent 4 }}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: {{ .Release.Name }}
  template:
    metadata:
      labels:
        {{- include "static-site.labels" . | nindent 8 }}
    spec:
      automountServiceAccountToken: false
      enableServiceLinks: false
      securityContext:
        runAsNonRoot: true
        runAsUser: 65532
        runAsGroup: 65532
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: caddy
          image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}{{ with .Values.image.digest }}@{{ . }}{{ end }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            privileged: false
            capabilities:
              drop: [ALL]
          ports:
            - name: http
              containerPort: ${SITE_PORT}
              protocol: TCP
          readinessProbe:
            httpGet:
              path: /
              port: http
            periodSeconds: 5
          livenessProbe:
            httpGet:
              path: /
              port: http
            initialDelaySeconds: 5
            periodSeconds: 20
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
`,
  'templates/service.yaml': `apiVersion: v1
kind: Service
metadata:
  name: {{ .Release.Name }}
  labels:
    {{- include "static-site.labels" . | nindent 4 }}
spec:
  type: ClusterIP
  selector:
    app: {{ .Release.Name }}
  ports:
    - name: http
      port: {{ .Values.service.port }}
      targetPort: http
      protocol: TCP
`,
  'templates/ingress.yaml': `{{- if .Values.ingress.host }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}
  labels:
    {{- include "static-site.labels" . | nindent 4 }}
  {{- with .Values.ingress.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  {{- with .Values.ingress.className }}
  ingressClassName: {{ . | quote }}
  {{- end }}
  rules:
    - host: {{ .Values.ingress.host | quote }}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}
                port:
                  name: http
  {{- with .Values.ingress.tlsSecret }}
  tls:
    - hosts:
        - {{ $.Values.ingress.host | quote }}
      secretName: {{ . | quote }}
  {{- end }}
{{- end }}
`,
  'templates/networkpolicy.yaml': `{{- if .Values.networkPolicy.enabled }}
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}
  labels:
    {{- include "static-site.labels" . | nindent 4 }}
spec:
  podSelector:
    matchLabels:
      app: {{ .Release.Name }}
  policyTypes:
    - Ingress
    - Egress
  ingress:
    - from:
        {{- toYaml .Values.networkPolicy.from | nindent 8 }}
      ports:
        - port: ${SITE_PORT}
          protocol: TCP
  egress: []
{{- end }}
`,
};

/** Writes the chart into `<dir>/${SITE_CHART_NAME}` and returns that directory. */
export function writeSiteChart(dir: string): string {
  const root = join(dir, SITE_CHART_NAME);
  for (const [path, text] of Object.entries(SITE_CHART)) {
    const file = join(root, path);
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, text);
  }
  return root;
}
