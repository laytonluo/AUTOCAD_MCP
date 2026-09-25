"""Analyze a COLDUMP export (COL-L / AN-COL / A5-COL) and write a column preview CSV.

Usage: python analyze_col_csv.py <dwg>_col_layers.csv [preview.csv]

- COL-L   : LINE / LWPOLYLINE chained into closed loops (endpoint tolerance TOL).
- AN-COL  : column IDs, paired to loop centroids (nearest, each label used once, <= MAX_ID_DIST).
- A5-COL  : size tags such as "(90x110)", paired to the nearest AN-COL label (tags sit under the ID).
- Size tag W x H is compared with the loop's X extent x Y extent; 90x110 and 110x90 are different types.
"""
import csv, math, re, sys
from collections import defaultdict

TOL = 0.1           # drawing units (cm in this drawing)
MAX_ID_DIST = 250   # max centroid -> ID label distance
GRID_GAP = 300      # centroids closer than this share a grid line

SIZE_RE = re.compile(r"^\(?\s*(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)\s*\)?$")


def load(path):
    for enc in ("utf-8-sig", "big5", "cp950"):
        try:
            with open(path, encoding=enc) as f:
                return list(csv.DictReader(f))
        except UnicodeDecodeError:
            continue
    raise SystemExit("cannot decode " + path)


def segments(rows):
    segs = []
    for r in rows:
        if r["type"] == "LINE":
            segs.append(((float(r["x"]), float(r["y"])), (float(r["x2"]), float(r["y2"])), r["handle"]))
        elif r["type"] == "LWPOLYLINE" and r["vertices"]:
            pts = [tuple(map(float, v.split())) for v in r["vertices"].split(";")]
            if r["closed"] == "1":
                pts.append(pts[0])
            segs += [(a, b, r["handle"]) for a, b in zip(pts, pts[1:])]
    return segs


def loops(segs):
    key = lambda p: (round(p[0] / TOL), round(p[1] / TOL))
    adj = defaultdict(list)
    for i, (a, b, _) in enumerate(segs):
        adj[key(a)].append(i)
        adj[key(b)].append(i)
    seen, out = set(), []
    for i in range(len(segs)):
        if i in seen:
            continue
        comp, stack = [], [i]
        while stack:
            j = stack.pop()
            if j in seen:
                continue
            seen.add(j)
            comp.append(j)
            for p in segs[j][:2]:
                stack += adj[key(p)]
        pts = [p for j in comp for p in segs[j][:2]]
        xs, ys = [p[0] for p in pts], [p[1] for p in pts]
        closed = all(len(adj[key(p)]) == 2 for p in pts)
        ortho = all(abs(segs[j][0][0] - segs[j][1][0]) < TOL or abs(segs[j][0][1] - segs[j][1][1]) < TOL for j in comp)
        out.append(dict(
            handles=sorted({segs[j][2] for j in comp}), closed=closed,
            rect=closed and len(comp) == 4 and ortho,
            cx=(min(xs) + max(xs)) / 2, cy=(min(ys) + max(ys)) / 2,
            W=round(max(xs) - min(xs), 1), H=round(max(ys) - min(ys), 1)))
    return out


def grid(values):
    values = sorted(values)
    groups = [[values[0]]]
    for v in values[1:]:
        (groups[-1].append(v) if v - groups[-1][-1] < GRID_GAP else groups.append([v]))
    return [sum(g) / len(g) for g in groups]


def main(src, dst):
    rows = load(src)
    by = lambda layer: [r for r in rows if r["layer"] == layer]
    cols = loops(segments(by("COL-L")))
    ids = [dict(h=r["handle"], x=float(r["x"]), y=float(r["y"]), t=r["text"]) for r in by("AN-COL")]
    tags = [dict(h=r["handle"], x=float(r["x"]), y=float(r["y"]), t=r["text"]) for r in by("A5-COL")]

    # ID label -> column: nearest first, each used once
    cand = sorted((math.hypot(l["x"] - c["cx"], l["y"] - c["cy"]), li, ci)
                  for li, l in enumerate(ids) for ci, c in enumerate(cols))
    used_l, used_c = set(), set()
    for d, li, ci in cand:
        if li in used_l or ci in used_c or d > MAX_ID_DIST:
            continue
        used_l.add(li)
        used_c.add(ci)
        cols[ci]["id"], cols[ci]["id_h"], cols[ci]["id_d"] = ids[li]["t"], ids[li]["h"], round(d, 1)
    extra_ids = [ids[i] for i in range(len(ids)) if i not in used_l]

    # size tag -> nearest ID label
    tag_of = {}
    for s in tags:
        best = min(ids, key=lambda l: math.hypot(l["x"] - s["x"], l["y"] - s["y"]))
        tag_of.setdefault(best["h"], s)

    X, Y = grid([c["cx"] for c in cols]), grid([c["cy"] for c in cols])
    out = []
    for c in cols:
        ix = min(range(len(X)), key=lambda i: abs(X[i] - c["cx"])) + 1
        iy = len(Y) - min(range(len(Y)), key=lambda i: abs(Y[i] - c["cy"]))
        tag = tag_of.get(c.get("id_h"), {})
        m = SIZE_RE.match(tag.get("t", ""))
        tw, th = (float(m[1]), float(m[2])) if m else (None, None)
        if not c["closed"]:
            status, msg = "排除", "輪廓未封閉"
        elif not c["rect"]:
            status, msg = "人工複核", "非矩形"
        elif "id" not in c:
            status, msg = "無編號", ""
        elif not m:
            status, msg = "無尺寸", "找不到 A5-COL 尺寸標籤"
        elif (tw, th) == (c["W"], c["H"]):
            status, msg = "已配對", ""
        else:
            status, msg = "資料矛盾", f"標籤 {tw:g}x{th:g} ≠ 量測 {c['W']:g}x{c['H']:g}"
        out.append({
            "PositionKey": f"X{ix}-Y{iy}", "ColumnId": c.get("id", ""), "Geometry": "矩形" if c["rect"] else "其他",
            "MeasuredWxH": f"{c['W']:g}x{c['H']:g}", "TagWxH": f"{tw:g}x{th:g}" if m else "",
            "FamilyType": f"C{c['W']:g}x{c['H']:g}cm" if status == "已配對" else "",
            "Status": status, "Message": msg,
            "CenterX": round(c["cx"], 4), "CenterY": round(c["cy"], 4),
            "IdHandle": c.get("id_h", ""), "IdDist": c.get("id_d", ""), "TagHandle": tag.get("h", ""),
            "LineHandles": " ".join(c["handles"]),
        })
    out.sort(key=lambda o: (int(o["PositionKey"].split("-Y")[1]), o["PositionKey"]))
    with open(dst, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=list(out[0]))
        w.writeheader()
        w.writerows(out)

    print(f"COL-L loops: {len(cols)} (closed {sum(c['closed'] for c in cols)}, rect {sum(c['rect'] for c in cols)})")
    print(f"AN-COL: {len(ids)}  A5-COL: {len(tags)}")
    for o in out:
        print(o["PositionKey"], o["ColumnId"], o["MeasuredWxH"], o["TagWxH"], o["Status"], o["Message"])
    for l in extra_ids:
        print(f"未使用的柱編號標籤: {l['t']} (handle {l['h']})")
    print("saved", dst)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "col_preview.csv")
