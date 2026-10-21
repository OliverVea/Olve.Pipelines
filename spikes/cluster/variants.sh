# variant name -> command prefix (sourced by local-matrix.sh and the pod script)
V_glibc_native="/olve/step-glibc"
V_glibc_loader="/olve/glibc/ld-linux-x86-64.so.2 --library-path /olve/glibc /olve/step-glibc"
V_glibc_patched="/olve/step-glibc-patched  # patchelf --set-interpreter /olve/glibc/... --set-rpath /olve/glibc (segfaults)"
V_musl_native="/olve/step-musl"
V_musl_loader="/olve/musl/ld-musl-x86_64.so.1 --library-path /olve/musl /olve/step-musl"
V_musl_patched="/olve/step-musl-patched  # patchelf --set-interpreter /olve/musl/... (segfaults)"
