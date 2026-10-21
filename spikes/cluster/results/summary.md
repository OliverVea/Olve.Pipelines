| case | run | copy s | size | deps in /olve/lib | cold exec->JS ms | warm exec->JS ms | child rc(3) | spawn avg ms | net npm/registry/garage | custom child |
|---|---|---|---|---|---|---|---|---|---|---|
| alpine-glibc-mem | OK | 0.03 | 89.5M | yes | 20 | 20 | 3 | 1.9 | 200/100ms 200/11ms 403/3ms | None |
| alpine-glibc | OK | 0.05 | 89.5M | yes | 10 | 20 | 3 | 2.1 | 200/108ms 200/8ms 403/2ms | None |
| alpine-musl-mem | OK | 0.05 | 84.5M | yes | 70 | 20 | 3 | 1.7 | 200/91ms 200/7ms 403/2ms | None |
| alpine-musl | OK | 0.12 | 84.5M | yes | 40 | 20 | 3 | 1.6 | 200/137ms 200/24ms 403/4ms | None |
| debian-glibc | OK | 0.1 | 89.5M | yes | 20 | 20 | 3 | 2.5 | 200/109ms 200/8ms 403/3ms | None |
| debian-musl | OK | 0.07 | 84.5M | yes | 20 | 20 | 3 | 2.1 | 200/117ms 200/7ms 403/2ms | None |
| distroless-glibc | OK | 0.06 | 89.5M | yes | 20 | 10 | no shell | None | 200/129ms 200/6ms 403/2ms | None |
| distroless-musl | OK | 0.11 | 84.5M | yes | 20 | 20 | no shell | None | 200/137ms 200/9ms 403/3ms | None |
| dotnet-glibc | OK | 0.12 | 89.5M | yes | 40 | 20 | 3 | 2.6 | 200/133ms 200/10ms 403/3ms | ['10.0.301', 'git version 2.43.0'] |
| dotnet-musl | OK | 0.08 | 84.5M | yes | 30 | 20 | 3 | 2.6 | 200/120ms 200/6ms 403/3ms | ['10.0.301', 'git version 2.43.0'] |
| kaniko-glibc | OK | 0.06 | 89.5M | yes | 30 | 30 | 3 | 1.6 | 200/94ms 200/6ms 403/2ms | ['Kaniko version :  v1.24.0'] |
| kaniko-musl | OK | 0.08 | 84.5M | yes | 40 | 30 | 3 | 1.4 | 200/95ms 200/9ms 403/3ms | ['Kaniko version :  v1.24.0'] |
