import csv, glob, os, sys, collections, statistics, re
import xml.etree.ElementTree as ET

O = sys.argv[1]
rows = list(csv.DictReader(open(f"{O}/runs.csv")))
ph = collections.defaultdict(list)
for r in rows:
    ph[r["phase"]].append(r)
print("phase runs nonzero median_s max_s")
for p, rs in ph.items():
    w = [float(r["wall_s"]) for r in rs]
    print(f"{p:11} {len(rs):3} {sum(r['exit'] != '0' for r in rs):3} {statistics.median(w):7.2f} {max(w):7.2f}")

fails = collections.Counter()
reasons = {}
dur = collections.defaultdict(list)
parsed = collections.Counter()
for d in sorted(glob.glob(f"{O}/*-*")):
    phase = os.path.basename(d).split("-")[0]
    trx = glob.glob(f"{d}/**/*.trx", recursive=True)
    if not trx:
        print("no trx", d)
        continue
    root = ET.parse(trx[0]).getroot()
    ns = {"t": root.tag.split("}")[0].strip("{")}
    n = 0
    for r in root.findall(".//t:UnitTestResult", ns):
        n += 1
        name = r.get("testName")
        out = r.get("outcome")
        m = re.match(r"(\d+):(\d+):([\d.]+)", r.get("duration") or "0:0:0")
        if m:
            s = int(m[1]) * 3600 + int(m[2]) * 60 + float(m[3])
            if phase == "idle":
                dur[name].append(s)
        if out != "Passed":
            fails[(phase, name, out)] += 1
            msg = r.find(".//t:Message", ns)
            reasons[(phase, name)] = (msg.text or "")[:220] if msg is not None else ""
    parsed[(phase, n)] += 1

print("trx (phase, tests):", dict(parsed))
print("non-passing:", sum(fails.values()))
for k, v in fails.most_common(30):
    print(v, k, reasons.get(k[:2], ""))

med = sorted(((statistics.median(v), n) for n, v in dur.items()), reverse=True)
tot = sum(m for m, _ in med)
print(f"\nsum of median test time {tot:.1f}s over {len(med)} tests")
for m, n in med[:15]:
    print(f"{m:7.3f}s {n[:95]}")
print("tests >= 100ms:", sum(1 for m, _ in med if m >= 0.1), "time in them:", round(sum(m for m, _ in med if m >= 0.1), 1))
