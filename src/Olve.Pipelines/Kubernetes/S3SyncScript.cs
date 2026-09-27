namespace Olve.Pipelines.Kubernetes;

/// <summary>
/// POSIX-sh bundle sync run by the step pod's s3-download / s3-upload helper containers
/// (<c>curlimages/curl</c>). Replaces <c>mc mirror</c> (MinIO's client is archived and its images are
/// gone): each object is one SigV4-signed curl request, keys are percent-encoded, and listing pages
/// through ListObjectsV2. Invoked as <c>sh -c &lt;script&gt; s3sync upload|download</c>; config comes from
/// env (see the header). Any failed request exits non-zero, which fails the Job.
/// </summary>
internal static class S3SyncScript
{
    public const string Content = """
        # s3sync.sh <upload|download> — mirror /output -> s3://$S3_BUCKET/$S3_PREFIX/ or s3://.../$S3_PREFIX/ -> /input.
        # Env: S3_ENDPOINT, S3_BUCKET, S3_PREFIX, S3_ACCESS_KEY, S3_SECRET_KEY, [S3_SESSION_TOKEN], [S3_INSECURE=1].
        set -eu
        enc() { # RFC 3986 percent-encode; keep '/' when $2 = path
          printf '%s' "$1" | od -An -v -tx1 | tr -s ' \n' '  ' | tr ' ' '\n' | while read -r h; do
            [ -z "$h" ] && continue
            case "$h" in
              2d|2e|5f|7e|3[0-9]|4[1-9a-f]|5[0-9a]|6[1-9a-f]|7[0-9a]) printf "\\$(printf '%03o' "0x$h")" ;;
              2f) if [ "${2:-}" = path ]; then printf /; else printf %%2F; fi ;;
              *) printf '%%%s' "$(echo "$h" | tr a-f A-F)" ;;
            esac
          done
        }
        unxml() { sed -e 's/&lt;/</g' -e 's/&gt;/>/g' -e "s/&apos;/'/g" -e 's/&quot;/"/g' -e 's/&#34;/"/g' -e "s/&#39;/'/g" -e 's/&amp;/\&/g'; }
        s3() { # s3 <curl args...> : SigV4-signed request
          set -- -sS --fail-with-body --retry 3 --aws-sigv4 "aws:amz:us-east-1:s3" --user "$S3_ACCESS_KEY:$S3_SECRET_KEY" \
            -H "x-amz-content-sha256: UNSIGNED-PAYLOAD" "$@"
          [ -n "${S3_SESSION_TOKEN:-}" ] && set -- -H "x-amz-security-token: $S3_SESSION_TOKEN" "$@"
          [ "${S3_INSECURE:-}" = 1 ] && set -- -k "$@"
          curl "$@"
        }
        base="${S3_ENDPOINT%/}/$S3_BUCKET"
        prefix="${S3_PREFIX%/}/"
        case "$1" in
          upload)
            cd /output
            find . -type f | sed 's|^\./||' | while IFS= read -r f; do
              s3 -o /dev/null -T "$f" "$base/$(enc "$prefix$f" path)"
            done ;;
          download)
            token=
            while :; do
              q="list-type=2&prefix=$(enc "$prefix")"
              [ -n "$token" ] && q="continuation-token=$(enc "$token")&$q"
              page=$(s3 "$base?$q")
              printf '%s' "$page" | grep -o '<Key>[^<]*</Key>' | sed -e 's|^<Key>||' -e 's|</Key>$||' | unxml | while IFS= read -r k; do
                rel="${k#"$prefix"}"
                mkdir -p "/input/$(dirname "$rel")"
                s3 -o "/input/$rel" "$base/$(enc "$k" path)"
              done
              printf '%s' "$page" | grep -q '<IsTruncated>true</IsTruncated>' || break
              token=$(printf '%s' "$page" | grep -o '<NextContinuationToken>[^<]*</NextContinuationToken>' | sed -e 's|^<NextContinuationToken>||' -e 's|</NextContinuationToken>$||' | unxml)
            done ;;
          *) echo "usage: s3sync.sh upload|download" >&2; exit 2 ;;
        esac
        """;
}
