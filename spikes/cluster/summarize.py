import json, re, sys, pathlib
rows = []
for f in sorted(pathlib.Path(sys.argv[1]).glob("*.log")):
    t = f.read_text()
    sec = dict(re.findall(r"### (\w+)\n(.*?)(?=\n### |\Z)", t, re.S))
    copy = re.search(r"COPY_SECONDS=(\S+) SIZE=(\S+)", sec.get("harness", ""))
    lib = sec.get("ldlist", "")
    hermetic = all("/olve/lib/" in l or "ld-musl" in l and "=>" not in l or "linux-vdso" in l or "libc.musl" in l for l in lib.strip().splitlines() if l.strip())
    def j(k):
        m = re.search(r"^\{.*\}$", sec.get(k, ""), re.M)
        return json.loads(m.group(0)) if m else None
    c, s = j("cold"), j("step")
    ok = bool(s)
    ch = (s or {}).get("children", {})
    net = " ".join(f"{n.get('status', 'ERR')}/{n['ms']}ms" for n in (s or {}).get("net", []))
    custom = ch.get("custom", {}).get("out") if ch.get("custom") else None
    rows.append([f.stem, "OK" if ok else "FAIL", copy and copy.group(1), copy and copy.group(2), "yes" if hermetic else "NO",
                 c and c["execToJsMs"], s and s["execToJsMs"], ch.get("basic", {}).get("code") if ch.get("basic") else ch.get("skipped"),
                 ch.get("spawn20AvgMs"), net, custom])
hdr = ["case", "run", "copy s", "size", "deps in /olve/lib", "cold exec->JS ms", "warm exec->JS ms", "child rc(3)", "spawn avg ms", "net npm/registry/garage", "custom child"]
print("| " + " | ".join(hdr) + " |"); print("|" + "---|" * len(hdr))
for r in rows: print("| " + " | ".join(str(x) for x in r) + " |")
if any(r[1] != "OK" for r in rows): print("\nFAILURES present"); 
