import json, sys, collections, re, pickle

full_path, pilot_path, out = sys.argv[1:4]
full = json.load(open(full_path))
pilot = json.load(open(pilot_path))


def short(p):
    return p.split("/src/backend/Olve.Pipelines/")[-1]


def tests_of(r):
    t = {}
    for p, f in (r.get("testFiles") or {}).items():
        for x in f.get("tests", []):
            t[x["id"]] = (x.get("name"), p.split("/Olve.Pipelines.UnitTests/")[-1])
    return t


tests = tests_of(full)
ptests = tests_of(pilot)
print("tests", len(tests), "distinct names", len({v[0] for v in tests.values()}))


def area(s):
    parts = s.split("/")
    if parts[0] == "Pipelines" and len(parts) > 2:
        return "Pipelines/" + parts[1]
    return parts[0] if len(parts) > 1 else "(root)"


st = collections.Counter()
by_area = collections.defaultdict(collections.Counter)
by_file = collections.defaultdict(collections.Counter)
kills = collections.defaultdict(set)
cover = collections.defaultdict(set)
mutants = {}
for p, f in full["files"].items():
    s = short(p)
    for m in f["mutants"]:
        st[m["status"]] += 1
        by_area[area(s)][m["status"]] += 1
        by_file[s][m["status"]] += 1
        mid = (s, m["id"])
        mutants[mid] = m
        for t in m.get("killedBy") or []:
            kills[t].add(mid)
        for t in m.get("coveredBy") or []:
            cover[t].add(mid)

print("status", dict(st))


def score(c):
    d = c["Killed"] + c["Timeout"]
    u = c["Survived"] + c["NoCoverage"]
    return 100 * d / (d + u) if d + u else float("nan"), d + u


print("\n== areas (score, measured, K S N T Compile Ignored)")
for a, c in sorted(by_area.items(), key=lambda kv: score(kv[1])[0]):
    sc, n = score(c)
    print(f"{a:34}{sc:6.1f}% n={n:5} K{c['Killed']} S{c['Survived']} N{c['NoCoverage']} T{c['Timeout']} C{c['CompileError']} I{c['Ignored']}")

print("\n== files with >=30 undetected, worst by undetected")
rows = [(c["Survived"] + c["NoCoverage"], f, c) for f, c in by_file.items()]
for u, f, c in sorted(rows, key=lambda x: -x[0])[:30]:
    print(f"{f:62}{score(c)[0]:6.1f}% undetected={u} S{c['Survived']} N{c['NoCoverage']} C{c['CompileError']}")

print("\n== compile-error share, top files")
for f, c in sorted(by_file.items(), key=lambda kv: -kv[1]["CompileError"])[:12]:
    print(f"{f:62} C{c['CompileError']}  measured={score(c)[1]}")

# Repeatability: Manifests mutants were tested in both runs.
print("\n== repeatability pilot vs full (Manifests)")


def key(s, m):
    l = m["location"]
    return (s, l["start"]["line"], l["start"]["column"], l["end"]["line"], l["end"]["column"], m["mutatorName"], m.get("replacement"))


pm = {}
for p, f in pilot["files"].items():
    s = short(p)
    if not s.startswith("Pipelines/Manifests"):
        continue
    for m in f["mutants"]:
        pm[key(s, m)] = m
same = diff = setsame = setdiff = 0
flips = collections.Counter()
flip_tests = collections.Counter()
for mid, m in mutants.items():
    k = key(mid[0], m)
    if k not in pm:
        continue
    a = pm[k]
    if a["status"] in ("Ignored", "CompileError") and m["status"] in ("Ignored", "CompileError"):
        continue
    if a["status"] == m["status"]:
        same += 1
    else:
        diff += 1
        flips[(a["status"], m["status"])] += 1
    if a["status"] == m["status"] == "Killed":
        x = {ptests[t][0] for t in a.get("killedBy") or []}
        y = {tests[t][0] for t in m.get("killedBy") or []}
        if x == y:
            setsame += 1
        else:
            setdiff += 1
            for t in x ^ y:
                flip_tests[t] += 1
print("status same", same, "diff", diff, dict(flips))
print("killer-set same", setsame, "diff", setdiff)
print("tests most often differing:", flip_tests.most_common(12))

# Redundancy
print("\n== redundancy")
ids = list(tests)
zero = [t for t in ids if not kills[t]]
print("zero kills:", len(zero), "of which cover nothing:", sum(1 for t in zero if not cover[t]))
killers_of = collections.defaultdict(set)
for t, ks in kills.items():
    for m in ks:
        killers_of[m].add(t)
unique = {t: {m for m in kills[t] if len(killers_of[m]) == 1} for t in ids}
no_unique = [t for t in ids if kills[t] and not unique[t]]
print("with kills:", len(ids) - len(zero), "with >=1 unique kill:", sum(1 for t in ids if unique[t]), "no unique kill:", len(no_unique))
print("mutants killed:", len(killers_of), "with sole killer:", sum(1 for v in killers_of.values() if len(v) == 1))

groups = collections.defaultdict(list)
for t in ids:
    if kills[t]:
        groups[frozenset(kills[t])].append(t)
dup = [v for v in groups.values() if len(v) > 1]
print("identical-kill groups:", len(dup), "tests in them:", sum(map(len, dup)), "removable if one kept:", sum(len(v) - 1 for v in dup))

# Greedy minimum cover keeping every killed mutant killed.
remaining = set(killers_of)
chosen = []
while remaining:
    t = max(ids, key=lambda x: len(kills[x] & remaining))
    gain = kills[t] & remaining
    if not gain:
        break
    chosen.append((t, len(gain)))
    remaining -= gain
print("greedy cover size:", len(chosen), "gain=1 tail:", sum(1 for _, g in chosen if g == 1))

# Kill concentration
sizes = sorted((len(kills[t]) for t in ids), reverse=True)
print("kills per test: max", sizes[0], "median", sizes[len(sizes) // 2], "top10 tests", sizes[:10])

by_tf = collections.defaultdict(lambda: [0, 0, 0, 0])
for t in ids:
    f = tests[t][1]
    r = by_tf[f]
    r[0] += 1
    r[1] += not kills[t]
    r[2] += bool(kills[t]) and not unique[t]
    r[3] += bool(unique[t])
print("\n== test files: tests zero nounique unique, worst first")
for f, r in sorted(by_tf.items(), key=lambda kv: -(kv[1][1] + kv[1][2]))[:25]:
    print(f"{f:58} {r}")

pickle.dump(dict(tests=tests, kills=dict(kills), cover=dict(cover), mutants=mutants, unique=unique, zero=zero, no_unique=no_unique, dup=dup, chosen=chosen), open(out, "wb"))
