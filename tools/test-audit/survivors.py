import pickle, sys, collections, re

d = pickle.load(open(sys.argv[1], "rb"))
root = sys.argv[2]
ms = d["mutants"]

c = collections.Counter(); s = collections.Counter(); n = collections.Counter(); k = collections.Counter()
for (f, i), m in ms.items():
    c[m["mutatorName"]] += 1
    if m["status"] == "Survived": s[m["mutatorName"]] += 1
    if m["status"] == "NoCoverage": n[m["mutatorName"]] += 1
    if m["status"] == "Killed": k[m["mutatorName"]] += 1
print("mutator: killed survived nocov")
for name, _ in s.most_common(14):
    print(f"{name:34} K{k[name]} S{s[name]} N{n[name]}")

src = {}


def line(f, n):
    if f not in src:
        src[f] = open(f"{root}/{f}").read().split("\n")
    return src[f][n - 1].strip()[:125]


for key, limit in [(x.split(":")[0], int(x.split(":")[1])) for x in sys.argv[3:]]:
    print(f"\n#### {key}")
    rows = []
    for (f, i), m in ms.items():
        if key in f and m["status"] in ("Survived", "NoCoverage"):
            if m["mutatorName"].startswith(("String", "Linq")) and "Mutation" in m["mutatorName"] and False:
                continue
            rows.append((m["location"]["start"]["line"], m["status"][0], m["mutatorName"][:24], m.get("replacement", "")[:50].replace("\n", " "), f))
    rows.sort()
    print(len(rows), "rows; showing", min(limit, len(rows)))
    step = max(1, len(rows) // limit)
    for r in rows[::step][:limit]:
        print(f"L{r[0]} {r[1]} {r[2]:24} -> {r[3]:50} | {line(r[4], r[0])}")
