{{/*
The server's settings: .Values.config, with .Values.auth and .Values.telemetry (when set) mapped onto the
Auth__* and OpenTelemetry__* keys. An empty value drops the key, as in .Values.config.
*/}}
{{- define "olve-pipelines.config" -}}
{{- $c := deepCopy .Values.config -}}
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
{{- range $k := list "OpenTelemetry__Endpoint" "OpenTelemetry__Protocol" "OpenTelemetry__OAuth2__TokenUrl" "OpenTelemetry__OAuth2__ClientId" "OpenTelemetry__OAuth2__Scope" -}}
{{- $_ := set $c $k "" -}}
{{- end -}}
{{- else if eq $exporter "otlp" -}}
{{- $_ := set $c "OpenTelemetry__Endpoint" .endpoint -}}
{{- $_ := set $c "OpenTelemetry__Protocol" (.protocol | default "http") -}}
{{- $auth := .auth | default dict -}}
{{- if eq ($auth.type | default "none") "oauth2" -}}
{{- $_ := set $c "OpenTelemetry__OAuth2__TokenUrl" $auth.tokenUrl -}}
{{- $_ := set $c "OpenTelemetry__OAuth2__ClientId" $auth.clientId -}}
{{- with $auth.scope -}}
{{- $_ := set $c "OpenTelemetry__OAuth2__Scope" . -}}
{{- end -}}
{{- else -}}
{{- $_ := set $c "OpenTelemetry__OAuth2__TokenUrl" "" -}}
{{- $_ := set $c "OpenTelemetry__OAuth2__ClientId" "" -}}
{{- end -}}
{{- else if $exporter -}}
{{- fail (printf "telemetry.exporter must be none or otlp, got %q" $exporter) -}}
{{- end -}}
{{- end -}}
{{- toYaml $c -}}
{{- end -}}

{{/*
The Kubernetes Secret holding the OTLP OAuth2 client secret ({name, key}) from telemetry.auth.clientSecret,
unless .Values.secrets already maps OpenTelemetry__OAuth2__ClientSecret.
*/}}
{{- define "olve-pipelines.telemetrySecret" -}}
{{- $mapped := false -}}
{{- range .Values.secrets -}}
{{- if hasKey (.envMap | default dict) "OpenTelemetry__OAuth2__ClientSecret" -}}
{{- $mapped = true -}}
{{- end -}}
{{- end -}}
{{- with .Values.telemetry -}}
{{- $auth := .auth | default dict -}}
{{- if and (eq (.exporter | default "") "otlp") (eq ($auth.type | default "") "oauth2") (not $mapped) -}}
{{- with ($auth.clientSecret | default dict).kubernetesSecret -}}
{{- toYaml (dict "name" .name "key" .key) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
