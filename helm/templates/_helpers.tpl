{{/*
The server's settings: .Values.config, with .Values.auth and .Values.telemetry (when set) mapped onto the
Auth__* and OpenTelemetry__* keys. An empty value drops the key, as in .Values.config. Unset, the runner
namespace is the release's and storage is the bundled Garage.
*/}}
{{- define "olve-pipelines.config" -}}
{{- $c := deepCopy .Values.config -}}
{{- if not (hasKey $c "Kubernetes__Namespace") -}}
{{- $_ := set $c "Kubernetes__Namespace" .Release.Namespace -}}
{{- end -}}
{{- if .Values.garage.enabled -}}
{{- if not (hasKey $c "Storage__Endpoint") -}}
{{- $_ := set $c "Storage__Endpoint" (printf "http://%s-garage.%s.svc.cluster.local:3900" .Release.Name .Release.Namespace) -}}
{{- end -}}
{{- if not (hasKey $c "Storage__Bucket") -}}
{{- $_ := set $c "Storage__Bucket" .Values.garage.bucket -}}
{{- end -}}
{{- end -}}
{{- with .Values.auth -}}
{{- $mode := .mode | default "" -}}
{{- if eq $mode "none" -}}
{{- $_ := set $c "Auth__Disable" "true" -}}
{{- else if eq $mode "dex" -}}
{{- $_ := set $c "Auth__Disable" "" -}}
{{- $_ := set $c "Auth__Authority" .issuer -}}
{{- $_ := set $c "Auth__Audience" .clientId -}}
{{- $_ := set $c "Auth__FrontendAuthority" .issuer -}}
{{- $_ := set $c "Auth__FrontendAudience" (.frontendClientId | default .clientId) -}}
{{- if hasPrefix "http://" .issuer -}}
{{- $_ := set $c "Auth__RequireHttpsMetadata" "false" -}}
{{- end -}}
{{- $allow := .allow | default dict -}}
{{- if not (or $allow.users $allow.orgs $allow.emails) -}}
{{- fail "auth.mode dex needs auth.allow (users, orgs or emails): an upstream such as GitHub lets anyone sign in" -}}
{{- end -}}
{{- range $i, $v := ($allow.users | default list) -}}
{{- $_ := set $c (printf "Auth__Allow__Users__%d" $i) $v -}}
{{- end -}}
{{- range $i, $v := ($allow.orgs | default list) -}}
{{- $_ := set $c (printf "Auth__Allow__Orgs__%d" $i) $v -}}
{{- end -}}
{{- range $i, $v := ($allow.emails | default list) -}}
{{- $_ := set $c (printf "Auth__Allow__Emails__%d" $i) $v -}}
{{- end -}}
{{- else if eq $mode "authentik" -}}
{{- $_ := set $c "Auth__Disable" "" -}}
{{- $_ := set $c "Auth__Authority" .authority -}}
{{- $_ := set $c "Auth__Audience" .audience -}}
{{- $_ := set $c "Auth__FrontendAuthority" .frontendAuthority -}}
{{- $_ := set $c "Auth__FrontendAudience" .frontendAudience -}}
{{- else if $mode -}}
{{- fail (printf "auth.mode must be none, dex or authentik, got %q" $mode) -}}
{{- end -}}
{{- if and (ne $mode "none") .anonymousReads -}}
{{- $_ := set $c "Auth__AnonymousReads" "true" -}}
{{- end -}}
{{- end -}}
{{- with .Values.telemetry -}}
{{- $exporter := .exporter | default "" -}}
{{- if eq $exporter "none" -}}
{{- $_ := set $c "OpenTelemetry__Endpoint" "" -}}
{{- $_ := set $c "OpenTelemetry__Protocol" "" -}}
{{- else if eq $exporter "otlp" -}}
{{- $_ := set $c "OpenTelemetry__Endpoint" .endpoint -}}
{{- $_ := set $c "OpenTelemetry__Protocol" (.protocol | default "http") -}}
{{- else if $exporter -}}
{{- fail (printf "telemetry.exporter must be none or otlp, got %q" $exporter) -}}
{{- end -}}
{{- end -}}
{{- if not (or (get $c "Auth__Authority") (get $c "Auth__SigningKey") (eq (toString (get $c "Auth__Disable")) "true")) -}}
{{- fail "auth is not configured: set auth (mode none, dex or authentik) or config.Auth__*" -}}
{{- end -}}
{{- toYaml $c -}}
{{- end -}}

{{/*
The Kubernetes Secret ({name, key}) holding the OTLP headers ("k1=v1,k2=v2") from telemetry.headers.
*/}}
{{- define "olve-pipelines.telemetryHeaders" -}}
{{- with .Values.telemetry -}}
{{- if eq (.exporter | default "") "otlp" -}}
{{- with (.headers | default dict).kubernetesSecret -}}
{{- toYaml (dict "name" .name "key" .key) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
