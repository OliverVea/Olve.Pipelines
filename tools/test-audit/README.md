# Test audit scripts

Analysis scripts behind [docs/design/test-audit.md](../../docs/design/test-audit.md). Run Stryker first (see that document); `<report>` is a `StrykerOutput/<timestamp>/reports/mutation-report.json`.

```bash
python3 analyze.py <full-report> <repeat-report> analysis.pkl   # scores, repeatability, redundancy
python3 lists.py analysis.pkl                                   # zero-kill tests, identical kill sets, top killers
python3 survivors.py analysis.pkl src/backend/Olve.Pipelines Scheduling/StageScheduler:30   # survivors by file:limit
python3 control_compare.py <full-report> <control-report>       # per-test run against the coverage-off control
dotnet build src/backend/Olve.Pipelines.UnitTests -c Release
bash flake.sh <output-dir>                                      # scaling, idle, loaded and concurrent suite runs
python3 flake_parse.py <output-dir>                             # failures and slowest tests
bash order-isolation.sh <results-dir> [jobs] [subset-runs] [seed]  # each class and method alone, random half-sets of classes
python3 apply-stryker-comments.py <worktree> safe-mode-triggers.json   # throwaway worktree only: disables the Safe Mode triggers
```

`analyze.py` needs two reports because it compares `Pipelines/Manifests` between runs; use a second run with `mutate` narrowed to that folder.

`triage-ledger.json` holds the verdict for each of the 1,061 non-string survivors, keyed by mutant id; a gap verdict names its row in the audit.
