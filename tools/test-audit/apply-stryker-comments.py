#!/usr/bin/env python3
"""Inline `/* Stryker disable once ... */` before each Safe Mode trigger, in a THROWAWAY worktree only.
Keeps line numbers, so report lines still match. Usage: apply-stryker-comments.py <worktree-root> <triggers.json>"""
import collections, json, os, sys
root, triggers = sys.argv[1:3]
by_file = collections.defaultdict(list)
for t in json.load(open(triggers)):
    by_file[t["file"]].append(t)
for rel, items in by_file.items():
    path = os.path.join(root, "src/backend/Olve.Pipelines", rel)
    lines = open(path, newline="").read().split("\n")
    for t in sorted(items, key=lambda t: (t["line"], t["col"]), reverse=True):
        i, c = t["line"] - 1, t["col"] - 1
        lines[i] = f'{lines[i][:c]}/* Stryker disable once {t["mutators"]}: definite-assignment guard */ {lines[i][c:]}'
    open(path, "w", newline="").write("\n".join(lines))
    print(f"{rel}: {len(items)}")
