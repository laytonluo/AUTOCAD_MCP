// revit_analyze.cs — 唯讀。柱線封閉環分析、柱編號／尺寸配對、族群類型比對、建立前預覽。不開 Transaction。
// 只修改「輸入」區塊。文字座標為 AutoCAD 的 DWG 座標（WCS）。
// Revit 2023 以前：new ElementId(xxxL) 改成 new ElementId((int)xxx)，.Id.Value 改成 .Id.IntegerValue。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

public static class DwgColAnalyze
{
    // ===== 輸入 =====
    const long VIEW_ID = 347452;
    const long IMPORT_ID = 351751;
    const string COL_LAYER = "COL-L";
    // 柱編號文字：「內容|X|Y」；Q3 選「不使用」時留空陣列
    static readonly string[] ID_LABELS = {
        // "C1|-15025.7432|-79524.8293",
    };
    // 尺寸來源：curve / tag / table
    const string SIZE_SOURCE = "table";
    // SIZE_SOURCE = tag：尺寸文字「內容|X|Y」
    static readonly string[] SIZE_LABELS = {
        // "(110x90)|-15028.5917|-79581.7991",
    };
    // SIZE_SOURCE = table：每列「柱編號,尺寸」
    const string TABLE = @"
";
    const double MAX_LABEL_DIST_MM = 2500;   // 標籤到柱質心（或尺寸到柱號）的最大距離
    const double GRID_MAX_DIST_MM = 3000;    // 柱中心到軸線的最大距離
    // ================

    const double FT = 304.8;                 // mm per ft
    const double TOL = 1.0 / FT;             // 1 mm 端點容差
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    class Seg { public XYZ A, B; public bool IsArc; public XYZ C; public double R; }
    class Loop
    {
        public string Kind = "other"; public bool Closed; public XYZ Ctr;
        public double W, H, D, Ang; public int Segs;
    }
    class Lbl { public string T; public XYZ P; }
    class Size { public string Kind; public double W, H, D; public string Raw; }

    public static string Execute(Document doc)
    {
        var view = doc.GetElement(new ElementId(VIEW_ID)) as View;
        var ii = doc.GetElement(new ElementId(IMPORT_ID)) as ImportInstance;
        if (view == null || ii == null) return "{\"error\":\"找不到視圖或 ImportInstance，請確認 ID\"}";
        Level level = view.GenLevel;
        if (level == null) return "{\"error\":\"視圖沒有 GenLevel，請切換到平面視圖\"}";

        // ---- 1. 柱線 → 封閉環（DWG 座標，ft） ----
        Transform instTf = Transform.Identity;
        var segs = new List<Seg>();
        var loops = new List<Loop>();
        GeometryElement ge = ii.get_Geometry(new Options());
        foreach (GeometryObject o in ge)
        {
            var gi = o as GeometryInstance;
            if (gi == null) continue;
            instTf = gi.Transform;
            Walk(doc, gi.GetSymbolGeometry(), Transform.Identity, segs, loops);
        }
        loops.AddRange(BuildLoops(segs));
        if (loops.Count == 0) return "{\"error\":\"圖層 " + COL_LAYER + " 沒有可用的柱線\"}";

        // ---- 2. 文字與 DWG 單位判斷 ----
        var ids = ParseLabels(ID_LABELS);
        var tags = SIZE_SOURCE == "tag" ? ParseLabels(SIZE_LABELS) : new List<Lbl>();
        var probe = ids.Count > 0 ? ids : tags;
        double maxFt = MAX_LABEL_DIST_MM / FT;
        string unitName = "-"; double s = 1; XYZ offset = XYZ.Zero;
        if (probe.Count > 0)
        {
            var units = new[] { "mm", "cm", "m", "inch", "ft" };
            var scales = new[] { 1 / FT, 10 / FT, 1000 / FT, 1.0 / 12, 1.0 };
            int best = -1; double bestScore = double.MinValue;
            for (int k = 0; k < scales.Length; k++)
            {
                double score = FitScore(probe, loops, scales[k], XYZ.Zero, maxFt);
                if (score > bestScore) { bestScore = score; best = k; }
            }
            unitName = units[best]; s = scales[best];
            if (Math.Floor(bestScore) < Math.Max(1, probe.Count / 2))
            {
                // DWG 原點與 symbol 原點不同時，以兩組點雲中心對齊
                XYZ mc = Mean(loops.Select(l => l.Ctr)), ml = Mean(probe.Select(p => p.P * s));
                offset = mc - ml;
                unitName += "+offset";
            }
            foreach (var l in ids) l.P = l.P * s + offset;
            foreach (var l in tags) l.P = l.P * s + offset;
        }

        // ---- 3. 柱編號配對 ----
        var colOfId = Pair(ids.Select(l => l.P).ToList(), loops.Select(l => l.Ctr).ToList(), maxFt);
        var idOfCol = Enumerable.Repeat(-1, loops.Count).ToArray();
        for (int i = 0; i < ids.Count; i++) if (colOfId[i] >= 0) idOfCol[colOfId[i]] = i;

        // ---- 4. 尺寸來源 ----
        var srcSize = new Size[loops.Count];
        var tableReport = new List<string>();
        var unusedTags = new List<Lbl>();
        if (SIZE_SOURCE == "tag")
        {
            int[] tgt;
            if (ids.Count > 0)
            {
                var toId = Pair(tags.Select(t => t.P).ToList(), ids.Select(l => l.P).ToList(), maxFt);
                tgt = toId.Select(i => i >= 0 ? colOfId[i] : -1).ToArray();
            }
            else tgt = Pair(tags.Select(t => t.P).ToList(), loops.Select(l => l.Ctr).ToList(), maxFt);
            for (int i = 0; i < tags.Count; i++)
            {
                if (tgt[i] >= 0) srcSize[tgt[i]] = ParseSize(tags[i].T);
                else unusedTags.Add(tags[i]);
            }
        }
        else if (SIZE_SOURCE == "table")
        {
            var map = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (string raw in TABLE.Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0) continue;
                n++;
                var parts = line.Split(new[] { ',', '\t', '，' }, 2);
                string key = parts[0].Trim(), val = parts.Length > 1 ? parts[1].Trim() : "";
                string st;
                if (key.Length == 0 || val.Length == 0) st = "缺值";
                else
                {
                    var sz = ParseSize(val);
                    if (sz == null) st = "無法識別";
                    else if (map.ContainsKey(key)) st = SameSize(map[key], sz) ? "重複" : "矛盾";
                    else { map[key] = sz; st = "成功"; }
                }
                tableReport.Add("{\"row\":" + n + ",\"id\":" + J.Q(key) + ",\"size\":" + J.Q(val) + ",\"status\":" + J.Q(st) + "}");
            }
            for (int c = 0; c < loops.Count; c++)
                if (idOfCol[c] >= 0 && map.ContainsKey(ids[idOfCol[c]].T)) srcSize[c] = map[ids[idOfCol[c]].T];
        }

        // ---- 5. 族群類型 ----
        var symbols = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var symByKey = new Dictionary<string, FamilySymbol>();
        foreach (var fs in symbols)
        {
            var sz = ParseSize(fs.Name);
            if (sz == null) continue;
            string k = Key(sz);
            if (!symByKey.ContainsKey(k)) symByKey[k] = fs;
        }

        // ---- 6. 軸線位置 ----
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => g.Curve is Line).ToList();

        // ---- 7. 組合預覽 ----
        double rotInst = Math.Atan2(instTf.BasisX.Y, instTf.BasisX.X);
        var rows = new List<string>();
        var commit = new List<string>();
        var count = new SortedDictionary<string, int>();
        var typeCount = new SortedDictionary<string, int>();
        var proj = loops.Select(l => instTf.OfPoint(l.Ctr)).ToList();
        var fallbackKeys = GridKeys(proj);
        for (int c = 0; c < loops.Count; c++)
        {
            Loop L = loops[c];
            string colId = idOfCol[c] >= 0 ? ids[idOfCol[c]].T : "";
            double idDist = idOfCol[c] >= 0 ? ids[idOfCol[c]].P.DistanceTo(L.Ctr) * FT : -1;
            var meas = L.Kind == "rect" ? new Size { Kind = "rect", W = R05(L.W * 30.48), H = R05(L.H * 30.48) }
                     : L.Kind == "circle" ? new Size { Kind = "circle", D = R05(L.D * 30.48) } : null;
            Size use = SIZE_SOURCE == "curve" ? meas : srcSize[c];
            string status, msg = "";
            FamilySymbol fs = null;
            if (!L.Closed) { status = "排除"; msg = "輪廓未封閉"; }
            else if (meas == null) { status = "人工複核"; msg = "非矩形／圓形，" + L.Segs + " 段"; }
            else if (SIZE_SOURCE != "curve" && use == null) { status = colId == "" ? "無編號" : "無尺寸"; msg = colId == "" ? "找不到柱編號" : "找不到尺寸"; }
            else if (!SameSize(use, meas))
            {
                status = "資料矛盾";
                msg = (use.Kind == "rect" && meas.Kind == "rect" && Math.Abs(use.W - meas.H) <= 0.5 && Math.Abs(use.H - meas.W) <= 0.5)
                      ? "寬高對調：" + Fmt(use) + " ≠ 量測 " + Fmt(meas) : Fmt(use) + " ≠ 量測 " + Fmt(meas);
            }
            else if (!symByKey.TryGetValue(Key(use), out fs)) { status = "無類型"; msg = "專案內沒有 " + TypeName(use); }
            else if (colId == "" && ID_LABELS.Length > 0) { status = "無編號"; msg = "可建立，但無柱編號可寫入"; }
            else status = "已配對";

            string pos = GridKey(grids, proj[c], fallbackKeys[c]);
            double rot = rotInst + (L.Kind == "rect" ? L.Ang : 0);
            count[status] = (count.ContainsKey(status) ? count[status] : 0) + 1;
            bool go = fs != null && (status == "已配對" || status == "無編號");
            if (go)
            {
                typeCount[fs.Name] = (typeCount.ContainsKey(fs.Name) ? typeCount[fs.Name] : 0) + 1;
                commit.Add(fs.Id.Value + "|" + J.N(proj[c].X) + "|" + J.N(proj[c].Y) + "|" + J.N(rot) + "|" + colId);
            }
            rows.Add("{\"create\":" + (go ? "true" : "false") + ",\"pos\":" + J.Q(pos) + ",\"id\":" + J.Q(colId)
                + ",\"geom\":" + J.Q(L.Kind == "rect" ? "矩形" : L.Kind == "circle" ? "圓形" : "人工複核")
                + ",\"measured\":" + J.Q(meas == null ? "" : Fmt(meas)) + ",\"source\":" + J.Q(use == null ? "" : Fmt(use))
                + ",\"type\":" + J.Q(fs == null ? "" : fs.FamilyName + ": " + fs.Name) + ",\"level\":" + J.Q(level.Name)
                + ",\"status\":" + J.Q(status) + ",\"msg\":" + J.Q(msg)
                + ",\"idDist_mm\":" + (idDist < 0 ? "null" : J.N(idDist)) + "}");
        }
        var unusedIds = ids.Where((l, i) => colOfId[i] < 0).Select(l => J.Q(l.T));

        var sb = new StringBuilder("{");
        sb.Append("\"view\":").Append(J.Q(view.Name)).Append(",\"level\":{\"id\":").Append(level.Id.Value).Append(",\"name\":").Append(J.Q(level.Name)).Append("}");
        sb.Append(",\"dwgUnit\":").Append(J.Q(unitName)).Append(",\"sizeSource\":").Append(J.Q(SIZE_SOURCE));
        sb.Append(",\"loops\":").Append(loops.Count).Append(",\"closed\":").Append(loops.Count(l => l.Closed));
        sb.Append(",\"summary\":{").Append(string.Join(",", count.Select(kv => J.Q(kv.Key) + ":" + kv.Value))).Append("}");
        sb.Append(",\"toCreateByType\":{").Append(string.Join(",", typeCount.Select(kv => J.Q(kv.Key) + ":" + kv.Value))).Append("}");
        sb.Append(",\"unusedIdLabels\":[").Append(string.Join(",", unusedIds)).Append("]");
        sb.Append(",\"unusedSizeLabels\":[").Append(string.Join(",", unusedTags.Select(t => J.Q(t.T)))).Append("]");
        sb.Append(",\"tableParse\":[").Append(string.Join(",", tableReport)).Append("]");
        sb.Append(",\"availableTypes\":[").Append(string.Join(",", symbols.Select(f => J.Q(f.Name)))).Append("]");
        sb.Append(",\"rows\":[").Append(string.Join(",", rows)).Append("]");
        sb.Append(",\"commitRows\":[").Append(string.Join(",", commit.Select(J.Q))).Append("]}");
        return sb.ToString();
    }

    // ---------- 幾何 ----------
    static void Walk(Document doc, GeometryElement ge, Transform tf, List<Seg> segs, List<Loop> loops)
    {
        foreach (GeometryObject o in ge)
        {
            var gi = o as GeometryInstance;
            if (gi != null) { Walk(doc, gi.GetSymbolGeometry(), tf.Multiply(gi.Transform), segs, loops); continue; }
            var gs = doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
            if (gs == null || gs.GraphicsStyleCategory == null || gs.GraphicsStyleCategory.Name != COL_LAYER) continue;
            if (o is Line)
            {
                var ln = (Line)o;
                segs.Add(new Seg { A = tf.OfPoint(ln.GetEndPoint(0)), B = tf.OfPoint(ln.GetEndPoint(1)) });
            }
            else if (o is PolyLine)
            {
                var pts = ((PolyLine)o).GetCoordinates().Select(tf.OfPoint).ToList();
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (pts[i].DistanceTo(pts[i + 1]) > TOL) segs.Add(new Seg { A = pts[i], B = pts[i + 1] });
            }
            else if (o is Arc)
            {
                var arc = (Arc)o;
                if (!arc.IsBound)
                    loops.Add(new Loop { Kind = "circle", Closed = true, Ctr = tf.OfPoint(arc.Center), D = 2 * arc.Radius, Segs = 1 });
                else
                    segs.Add(new Seg { A = tf.OfPoint(arc.GetEndPoint(0)), B = tf.OfPoint(arc.GetEndPoint(1)), IsArc = true, C = tf.OfPoint(arc.Center), R = arc.Radius });
            }
        }
    }

    static string PK(XYZ p) { return Math.Round(p.X / TOL).ToString(IC) + "," + Math.Round(p.Y / TOL).ToString(IC); }

    static List<Loop> BuildLoops(List<Seg> segs)
    {
        var adj = new Dictionary<string, List<int>>();
        for (int i = 0; i < segs.Count; i++)
            foreach (var p in new[] { segs[i].A, segs[i].B })
            {
                string k = PK(p);
                if (!adj.ContainsKey(k)) adj[k] = new List<int>();
                adj[k].Add(i);
            }
        var seen = new bool[segs.Count];
        var res = new List<Loop>();
        for (int i = 0; i < segs.Count; i++)
        {
            if (seen[i]) continue;
            var comp = new List<int>(); var st = new Stack<int>(); st.Push(i);
            while (st.Count > 0)
            {
                int j = st.Pop(); if (seen[j]) continue; seen[j] = true; comp.Add(j);
                foreach (var p in new[] { segs[j].A, segs[j].B }) foreach (int n in adj[PK(p)]) if (!seen[n]) st.Push(n);
            }
            var L = new Loop { Segs = comp.Count };
            L.Closed = comp.All(j => adj[PK(segs[j].A)].Count == 2 && adj[PK(segs[j].B)].Count == 2);
            var pts = comp.SelectMany(j => new[] { segs[j].A, segs[j].B }).ToList();
            L.Ctr = new XYZ((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2, 0);
            if (L.Closed && comp.Count == 4 && comp.All(j => !segs[j].IsArc))
            {
                var dirs = comp.Select(j => (segs[j].B - segs[j].A)).ToList();
                XYZ u = dirs[0].Normalize();
                bool ok = dirs.All(d => { double c = Math.Abs(d.Normalize().DotProduct(u)); return c < 1e-3 || c > 1 - 1e-3; });
                if (ok)
                {
                    if (Math.Abs(u.X) < Math.Abs(u.Y)) u = new XYZ(u.Y, -u.X, 0);
                    if (u.X < 0) u = u.Negate();
                    L.Kind = "rect"; L.Ang = Math.Atan2(u.Y, u.X);
                    L.W = dirs.Where(d => Math.Abs(d.Normalize().DotProduct(u)) > 0.5).Average(d => d.GetLength());
                    L.H = dirs.Where(d => Math.Abs(d.Normalize().DotProduct(u)) <= 0.5).Average(d => d.GetLength());
                    L.Ctr = Mean(pts);
                }
            }
            else if (L.Closed && comp.All(j => segs[j].IsArc))
            {
                var c0 = segs[comp[0]];
                if (comp.All(j => segs[j].C.DistanceTo(c0.C) < TOL && Math.Abs(segs[j].R - c0.R) < TOL))
                { L.Kind = "circle"; L.Ctr = c0.C; L.D = 2 * c0.R; }
            }
            res.Add(L);
        }
        return res;
    }

    // ---------- 配對 ----------
    static int[] Pair(List<XYZ> from, List<XYZ> to, double maxFt)
    {
        var cand = new List<Tuple<double, int, int>>();
        for (int i = 0; i < from.Count; i++)
            for (int j = 0; j < to.Count; j++)
            {
                double d = Flat(from[i]).DistanceTo(Flat(to[j]));
                if (d <= maxFt) cand.Add(Tuple.Create(d, i, j));
            }
        var res = Enumerable.Repeat(-1, from.Count).ToArray();
        var usedTo = new HashSet<int>();
        foreach (var c in cand.OrderBy(c => c.Item1))
            if (res[c.Item2] < 0 && !usedTo.Contains(c.Item3)) { res[c.Item2] = c.Item3; usedTo.Add(c.Item3); }
        return res;
    }

    static double FitScore(List<Lbl> lbls, List<Loop> loops, double s, XYZ off, double maxFt)
    {
        int hit = 0; double sum = 0;
        foreach (var l in lbls)
        {
            XYZ p = l.P * s + off;
            double d = loops.Min(c => Flat(p).DistanceTo(Flat(c.Ctr)));
            if (d <= maxFt) { hit++; sum += d; }
        }
        return hit - (hit > 0 ? sum / hit / maxFt * 0.5 : 0);   // 命中數優先，平均距離次之
    }

    static List<Lbl> ParseLabels(string[] arr)
    {
        var res = new List<Lbl>();
        foreach (string a in arr)
        {
            var p = a.Split('|');
            if (p.Length < 3) continue;
            res.Add(new Lbl { T = p[0].Trim(), P = new XYZ(double.Parse(p[1], IC), double.Parse(p[2], IC), 0) });
        }
        return res;
    }

    // ---------- 尺寸 ----------
    static readonly Regex RectRe = new Regex(@"(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)");
    static readonly Regex CircRe = new Regex(@"(?:^|[^A-Za-z])(?:C-)?[RDØøΦφ]\s*(\d+(?:\.\d+)?)");

    static Size ParseSize(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        double f = t.IndexOf("mm", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.1 : 1;
        var m = RectRe.Match(t);
        if (m.Success)
        {
            double w = double.Parse(m.Groups[1].Value, IC) * f, h = double.Parse(m.Groups[2].Value, IC) * f;
            return new Size { Kind = "rect", W = R05(w), H = R05(h), Raw = t };
        }
        m = CircRe.Match(t);
        if (m.Success) return new Size { Kind = "circle", D = R05(double.Parse(m.Groups[1].Value, IC) * f), Raw = t };
        return null;
    }

    static bool SameSize(Size a, Size b)
    {
        if (a == null || b == null || a.Kind != b.Kind) return false;
        return a.Kind == "rect" ? Math.Abs(a.W - b.W) <= 0.5 && Math.Abs(a.H - b.H) <= 0.5 : Math.Abs(a.D - b.D) <= 0.5;
    }
    static string Key(Size z) { return z.Kind == "rect" ? "R:" + J.N(z.W) + "x" + J.N(z.H) : "C:" + J.N(z.D); }
    static string Fmt(Size z) { return z.Kind == "rect" ? J.N(z.W) + "x" + J.N(z.H) : "D" + J.N(z.D); }
    static string TypeName(Size z) { return z.Kind == "rect" ? "C" + Fmt(z) + "cm" : "C-R" + J.N(z.D) + "cm"; }
    static double R05(double v) { return Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2; }

    // ---------- 位置識別 ----------
    static string GridKey(List<Grid> grids, XYZ p, string fallback)
    {
        double max = GRID_MAX_DIST_MM / FT;
        Grid gx = null, gy = null; double dx = max, dy = max;
        foreach (var g in grids)
        {
            var ln = (Line)g.Curve;
            XYZ d = ln.Direction;
            XYZ v = Flat(p) - Flat(ln.Origin);
            double dist = Math.Abs(v.X * d.Y - v.Y * d.X);   // 到無限長軸線的距離
            if (Math.Abs(d.Y) >= Math.Abs(d.X)) { if (dist < dx) { dx = dist; gx = g; } }
            else { if (dist < dy) { dy = dist; gy = g; } }
        }
        if (gx == null || gy == null) return fallback;
        string a = gx.Name, b = gy.Name;
        return (char.IsLetter(b[0]) && !char.IsLetter(a[0])) ? b + a : a + b;
    }

    static string[] GridKeys(List<XYZ> pts)
    {
        double gap = 1500 / FT;
        Func<IEnumerable<double>, List<double>> clus = vals =>
        {
            var v = vals.OrderBy(x => x).ToList(); var outp = new List<double>(); var cur = new List<double> { v[0] };
            for (int i = 1; i < v.Count; i++) { if (v[i] - cur.Last() < gap) cur.Add(v[i]); else { outp.Add(cur.Average()); cur = new List<double> { v[i] }; } }
            outp.Add(cur.Average()); return outp;
        };
        var X = clus(pts.Select(p => p.X)); var Y = clus(pts.Select(p => p.Y));
        return pts.Select(p =>
        {
            int ix = Enumerable.Range(0, X.Count).OrderBy(i => Math.Abs(X[i] - p.X)).First() + 1;
            int iy = Y.Count - Enumerable.Range(0, Y.Count).OrderBy(i => Math.Abs(Y[i] - p.Y)).First();
            return "X" + ix + "-Y" + iy;
        }).ToArray();
    }

    static XYZ Flat(XYZ p) { return new XYZ(p.X, p.Y, 0); }
    static XYZ Mean(IEnumerable<XYZ> ps)
    {
        var l = ps.ToList();
        return new XYZ(l.Average(p => p.X), l.Average(p => p.Y), 0);
    }
}

public static class J
{
    public static string Q(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
    public static string N(double v) { return Math.Round(v, 4).ToString(CultureInfo.InvariantCulture); }
}
