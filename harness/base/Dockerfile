# harness-base: the single init container of every v2 step pod ("design B").
#
#   /bin/busybox, /bin/sh ...   static busybox (musl build): sh, wget, tar, gzip, cp
#   /olve/lib/                  glibc loader + libs the bun --compile (bun-linux-x64) binary needs
#   /olve/bin/olve-init         entrypoint: fetch step.tar.gz + input.json into the shared emptyDir
#
# Stable across commits: the per-commit step binary is NOT in this image; olve-init downloads it
# from Garage via a pre-signed GET. Both bases are pinned by digest so the build is reproducible.
# See harness/README.md for the env contract and the pod template.

FROM debian:bookworm-slim@sha256:7ff6ba31527b9e336162fa061746c366c4b2032f4d2cff2078bea43f78efe3f8 AS glibc
RUN mkdir -p /out/lib \
 && cp -L /lib64/ld-linux-x86-64.so.2 \
          /lib/x86_64-linux-gnu/libc.so.6 \
          /lib/x86_64-linux-gnu/libpthread.so.0 \
          /lib/x86_64-linux-gnu/libdl.so.2 \
          /lib/x86_64-linux-gnu/libm.so.6 \
          /out/lib/ \
 && chmod 0755 /out/lib/*

FROM busybox:1.37-musl@sha256:5cec3fc171c87218698e85a52af7087de727372aae264a787b8112901a5b0092
LABEL org.opencontainers.image.source="https://github.com/OliverVea/Olve.Pipelines"
COPY --from=glibc /out/lib/ /olve/lib/
COPY --chmod=0755 olve-init /olve/bin/olve-init
ENTRYPOINT ["/olve/bin/olve-init"]
