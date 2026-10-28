import { table } from '../output';
import { DEFAULT_PROFILE, loadProfiles, resolveProfileName } from '../profiles';
import type { Command } from '../registry';

export const profileCommands: Command[] = [
  {
    noun: 'profile',
    verb: 'list',
    usesApi: false,
    summary: 'List the profiles in ~/.pl: instance, login state, which one is selected',
    detail: `pl profile list

  A profile is one instance and its login. Pick one with --profile <name> or
  PIPELINES_PROFILE; without either, commands use 'default'. Create or refresh one with
    pl login --profile beta --api-url https://pipelines-beta.ovea.pro`,
    async run(ctx) {
      const profiles = loadProfiles(ctx.configPath);
      const selected = resolveProfileName(ctx.args, ctx.env);
      const infos = Object.values(profiles)
        .sort(
          (a, b) =>
            Number(a.name !== DEFAULT_PROFILE) - Number(b.name !== DEFAULT_PROFILE) ||
            (a.name < b.name ? -1 : a.name > b.name ? 1 : 0),
        )
        .map((p) => ({
          name: p.name,
          apiUrl: p.apiUrl ?? null,
          loggedIn: !!p.auth?.accessToken,
          expiresAt: p.auth?.expiresAt ?? null,
          selected: p.name === selected,
        }));
      ctx.out.emit(infos, () => {
        if (infos.length === 0) {
          ctx.out.line(
            "No profiles yet. Create one with `pl login --api-url <instance>` (profile 'default').",
          );
          return;
        }
        ctx.out.lines(
          table(
            ['PROFILE', 'INSTANCE', 'LOGGED IN'],
            infos.map((i) => [
              (i.selected ? '* ' : '  ') + i.name,
              i.apiUrl ?? '(default instance)',
              i.loggedIn
                ? i.expiresAt
                  ? `yes, token until ${utc(i.expiresAt)}`
                  : 'yes'
                : 'no',
            ]),
          ),
        );
      });
    },
  },
];

/** `2026-10-28 05:12:00Z`, the way the C# `pl` printed expiry times. */
function utc(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d
    .toISOString()
    .replace('T', ' ')
    .replace(/\.\d{3}Z$/, 'Z');
}
