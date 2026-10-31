import json, sys, collections

full = json.load(open(sys.argv[1]))
ctrl = json.load(open(sys.argv[2]))


def short(p):
    return p.split("/src/backend/Olve.Pipelines/")[-1]


def tests_of(r):
    return {x["id"]: x["name"] for f in (r.get("testFiles") or {}).values() for x in f.get("tests", [])}


def key(s, m):
    l = m["location"]
    return (s, l["start"]["line"], l["start"]["column"], l["end"]["line"], l["end"]["column"], m["mutatorName"], m.get("replacement"))


ft, ct = tests_of(full), tests_of(ctrl)
print("control tests:", len(ct))
fm = {key(short(p), m): m for p, f in full["files"].items() for m in f["mutants"]}
tr = collections.Counter()
by_file = collections.defaultdict(collections.Counter)
extra = collections.Counter()
samekill = diffkill = 0
examples = []
for p, f in ctrl["files"].items():
    s = short(p)
    for m in f["mutants"]:
        if m["status"] in ("Ignored", "CompileError"):
            continue
        a = fm.get(key(s, m))
        if a is None:
            tr[("MISSING", m["status"])] += 1
            continue
        tr[(a["status"], m["status"])] += 1
        by_file[s][(a["status"], m["status"])] += 1
        if a["status"] != m["status"] and len(examples) < 14:
            examples.append((s, m["location"]["start"]["line"], a["status"], m["status"], [ct[t].split(".")[-1][:50] for t in (m.get("killedBy") or [])[:3]]))
        if a["status"] == m["status"] == "Killed":
            x = {ft[t] for t in a.get("killedBy") or []}
            y = {ct[t] for t in m.get("killedBy") or []}
            if x == y:
                samekill += 1
            else:
                diffkill += 1
                for t in y - x:
                    extra[t.replace("Olve.Pipelines.UnitTests.", "")] += 1

print("transitions (perTest -> coverage off):")
for k, v in tr.most_common():
    print(" ", k, v)
print("same killer set", samekill, "different", diffkill)
print("tests killing in control only:", extra.most_common(10))
for f, c in by_file.items():
    print(f, dict(c))
print("examples of disagreement:")
for e in examples:
    print(" ", e)
