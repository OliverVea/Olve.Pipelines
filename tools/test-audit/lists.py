import pickle, sys, collections

d = pickle.load(open(sys.argv[1], "rb"))
tests, kills, cover, unique = d["tests"], d["kills"], d["cover"], d["unique"]


def n(t):
    name, f = tests[t]
    return name.replace("Olve.Pipelines.UnitTests.", "")


print("== zero kills")
for t in sorted(d["zero"], key=n):
    print(f"cover={len(cover.get(t, ())):4} {n(t)}")

print("\n== identical kill groups (kills, tests)")
for g in sorted(d["dup"], key=lambda g: -len(g)):
    k = len(kills[g[0]])
    names = [n(t) for t in g]
    distinct = sorted(set(names))
    tag = "SAME-METHOD" if len(distinct) == 1 else "cross-method"
    print(f"k={k:4} x{len(g)} {tag}: " + " | ".join(distinct[:6]))

print("\n== heaviest killers")
for t in sorted(tests, key=lambda t: -len(kills.get(t, ())))[:12]:
    print(len(kills.get(t, ())), n(t))
