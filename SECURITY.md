# Security

Olve.Pipelines runs code from the repos it is bound to: build Jobs, step Jobs and pull requests' CI.
A flaw that lets that code reach more than it was given (another pipeline's secrets, the server's
storage, the cluster API beyond its RBAC), or lets someone outside the allow-list in, is a
vulnerability.

## Reporting

Report it privately through GitHub: **Security → Report a vulnerability** on
[the repository](https://github.com/OliverVea/Olve.Pipelines/security/advisories/new). Don't open a
public issue. Include what you did, what you reached, and the version (`pl --version`, and the
server's version on its overview page).

Fixes go out as a new release; the advisory is published once it is out.

## Supported versions

Only the latest release gets fixes.

## Hardening an instance

- Run step pods under gVisor (`Kubernetes:RuntimeClassName`, see
  [self-hosting](docs/setup/self-hosting.md)) when the repos it runs aren't all yours.
- Give pull requests CI only from authors you trust (`Ci:Authors`): CI runs the pull request's own
  code with the secrets its checks declare.
- Set the sign-in allow-list (`Auth:Allow:*`); with Dex and GitHub, anyone can otherwise sign in.
