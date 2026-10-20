// revit_cad_analyze.cs — 唯讀。DWG 只在 AutoCAD 開啟（未匯入 Revit）時使用。
// 讀取 COLDUMP 匯出的 CSV → 以軸線名稱把 CAD 座標對齊到 Revit 目前視圖的軸線 → 柱線分析、配對、類型比對、預覽。
// ALIGN_ONLY = true 時只做軸線對齊（Q2B 用）；false 時輸出完整預覽與 commitRows（給 revit_commit.cs）。
// Revit 2023 以前：new ElementId(xxxL) 改成 new ElementId((int)xxx)，.Id.Value 改成 .Id.IntegerValue。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

public static class DwgColCadAnalyze
{
    // ===== 輸入 =====
    const string CSV_PATH = @"D:\圖面\2F結構平面_col_layers.csv";
    const long VIEW_ID = 0;                  // 0＝目前作用中視圖
    const bool ALIGN_ONLY = true;
    const string GRID_LINE_LAYER = "AXIS";   // CAD 軸線圖層
    const string GRID_TEXT_LAYER = "AXIS-TXT"; // CAD 軸線編號圖層（TEXT／MTEXT／圖塊屬性），可與軸線圖層相同
    const string GRID_NAME_MAP = "";         // 名稱不同時：「CAD名=Revit名;CAD名=Revit名」，例如 "X1=1;Y1=A"
    const string COL_LAYER = "COL-L";
    const string ID_LAYER = "AN-COL";        // Q4 選「不使用」時填 ""
    const string SIZE_SOURCE = "tag";        // curve / tag / table
    const string SIZE_LAYER = "A5-COL";      // SIZE_SOURCE = tag 時使用
    const string TABLE = @"
";
    const double MAX_LABEL_DIST_MM = 2500;
    // ================

    const double FT = 304.8;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    class Row { public string Layer, Type, Text; public double X, Y, X2, Y2, R; public bool Closed; public string Verts; }
    class Seg { public double[] A, B; public bool IsArc; public double[] C; public double R; }
    class Loop { public string Kind = "other"; public bool Closed; public double[] Ctr; public double W, H, D, Ang; public int Segs; }
    class Lbl { public string T; public double[] P; }
    class Size { public string Kind; public double W, H, D; }
    class GLine { public string Name; public double[] P, Q; }

    public static string Execute(Document doc)
    {
        View view = VIEW_ID == 0 ? doc.ActiveView : doc.GetElement(new ElementId(VIEW_ID)) as View;
        if (view == null || view.GenLevel == null) return "{\"error\":\"視圖不是平面視圖或沒有樓層\"}";
        Level level = view.GenLevel;
        if (!File.Exists(CSV_PATH)) return "{\"error\":" + J.Q("找不到 CSV：" + CSV_PATH) + "}";
        var rows = ReadCsv(CSV_PATH);

        // ---- 單位（mm per CAD unit） ----
        string insunits = rows.Where(r => r.Type == "META").Select(r => r.Text).FirstOrDefault() ?? "";
        double u = 0; string unitSrc = "INSUNITS";
        switch (insunits.Replace("INSUNITS=", "")) { case "4": u = 1; break; case "5": u = 10; break; case "6": u = 1000; break; case "1": u = 25.4; break; case "2": u = 304.8; break; }

        // ---- CAD 柱線 ----
        var loops = BuildLoops(rows.Where(r => r.Layer == COL_LAYER).ToList());
        if (u == 0)
        {
            // 依柱寬中位數推測：20~200 → cm；200~2000 → mm；0.2~2 → m
            var ws = loops.Where(l => l.Kind == "rect").Select(l => Math.Min(l.W, l.H)).OrderBy(x => x).ToList();
            double med = ws.Count > 0 ? ws[ws.Count / 2] : 0;
            u = med >= 200 ? 1 : med >= 20 ? 10 : med >= 0.2 ? 1000 : 10;
            unitSrc = "柱寬推測";
        }

        // ---- 軸線對齊 ----
        var cadGrids = CadGrids(rows, u);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GRID_NAME_MAP.Split(';'))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2) map[kv[0].Trim()] = kv[1].Trim();
        }
        var rvtGrids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => g.Curve is Line)
            .Select(g => { var ln = (Line)g.Curve; return new GLine { Name = g.Name.Trim(), P = P2(ln.GetEndPoint(0)), Q = P2(ln.GetEndPoint(1)) }; })
            .ToList();
        var src = new List<double[]>(); var dst = new List<double[]>(); var ptNames = new List<string>();
        var cadNamed = cadGrids.Where(g => g.Name != null).ToList();
        for (int i = 0; i < cadNamed.Count; i++)
            for (int j = i + 1; j < cadNamed.Count; j++)
            {
                string ni = map.ContainsKey(cadNamed[i].Name) ? map[cadNamed[i].Name] : cadNamed[i].Name;
                string nj = map.ContainsKey(cadNamed[j].Name) ? map[cadNamed[j].Name] : cadNamed[j].Name;
                var ri = rvtGrids.FirstOrDefault(g => string.Equals(g.Name, ni, StringComparison.OrdinalIgnoreCase));
                var rj = rvtGrids.FirstOrDefault(g => string.Equals(g.Name, nj, StringComparison.OrdinalIgnoreCase));
                if (ri == null || rj == null) continue;
                var pc = Intersect(cadNamed[i], cadNamed[j]); var pr = Intersect(ri, rj);
                if (pc == null || pr == null) continue;
                src.Add(new[] { pc[0] * u / FT, pc[1] * u / FT }); dst.Add(pr); ptNames.Add(ni + "/" + nj);
            }

        var sb = new StringBuilder("{");
        sb.Append("\"view\":").Append(J.Q(view.Name)).Append(",\"level\":{\"id\":").Append(level.Id.Value).Append(",\"name\":").Append(J.Q(level.Name)).Append("}");
        sb.Append(",\"cadUnit_mm\":").Append(J.N(u)).Append(",\"unitSource\":").Append(J.Q(unitSrc));
        sb.Append(",\"cadGrids\":[").Append(string.Join(",", cadGrids.Select(g => J.Q(g.Name ?? "(未命名)")))).Append("]");
        sb.Append(",\"revitGrids\":[").Append(string.Join(",", rvtGrids.Select(g => J.Q(g.Name)))).Append("]");
        if (src.Count < 2)
            return sb.Append(",\"error\":\"可對應的軸線交點少於 2 個。請確認軸線圖層、軸線編號圖層，或以 GRID_NAME_MAP 指定名稱對應\"}").ToString();

        // 2D 相似轉換：旋轉 + 平移（比例固定為單位換算），並回報自由比例供檢查
        double[] ms = Mean(src), md = Mean(dst);
        double num = 0, den = 0, ss = 0;
        for (int k = 0; k < src.Count; k++)
        {
            double ax = src[k][0] - ms[0], ay = src[k][1] - ms[1], bx = dst[k][0] - md[0], by = dst[k][1] - md[1];
            num += ax * by - ay * bx; den += ax * bx + ay * by; ss += ax * ax + ay * ay;
        }
        double th = Math.Atan2(num, den);
        double freeScale = ss > 0 ? Math.Sqrt(num * num + den * den) / ss : 1;
        double c = Math.Cos(th), s = Math.Sin(th);
        double tx = md[0] - (c * ms[0] - s * ms[1]), ty = md[1] - (s * ms[0] + c * ms[1]);
        Func<double[], double[]> toRvt = p => { double x = p[0] * u / FT, y = p[1] * u / FT; return new[] { c * x - s * y + tx, s * x + c * y + ty }; };
        var resid = new List<string>(); double maxRes = 0;
        for (int k = 0; k < src.Count; k++)
        {
            double x = src[k][0], y = src[k][1];
            double ex = c * x - s * y + tx - dst[k][0], ey = s * x + c * y + ty - dst[k][1];
            double e = Math.Sqrt(ex * ex + ey * ey) * FT; maxRes = Math.Max(maxRes, e);
            resid.Add("{\"at\":" + J.Q(ptNames[k]) + ",\"err_mm\":" + J.N(e) + "}");
        }
        sb.Append(",\"align\":{\"points\":").Append(src.Count).Append(",\"rotation_deg\":").Append(J.N(th * 180 / Math.PI))
          .Append(",\"scaleCheck\":").Append(J.N(freeScale)).Append(",\"maxErr_mm\":").Append(J.N(maxRes))
          .Append(",\"residuals\":[").Append(string.Join(",", resid)).Append("]}");
        if (ALIGN_ONLY) return sb.Append("}").ToString();

        // ---- 柱編號、尺寸 ----
        double maxD = MAX_LABEL_DIST_MM / u;   // CAD 單位
        var ids = Labels(rows, ID_LAYER);
        var colOfId = Pair(ids.Select(l => l.P).ToList(), loops.Select(l => l.Ctr).ToList(), maxD);
        var idOfCol = Enumerable.Repeat(-1, loops.Count).ToArray();
        for (int i = 0; i < ids.Count; i++) if (colOfId[i] >= 0) idOfCol[colOfId[i]] = i;

        var srcSize = new Size[loops.Count];
        var tableReport = new List<string>();
        if (SIZE_SOURCE == "tag")
        {
            var tags = Labels(rows, SIZE_LAYER);
            int[] tgt = ids.Count > 0
                ? Pair(tags.Select(t => t.P).ToList(), ids.Select(l => l.P).ToList(), maxD).Select(i => i >= 0 ? colOfId[i] : -1).ToArray()
                : Pair(tags.Select(t => t.P).ToList(), loops.Select(l => l.Ctr).ToList(), maxD);
            for (int i = 0; i < tags.Count; i++) if (tgt[i] >= 0) srcSize[tgt[i]] = ParseSize(tags[i].T);
        }
        else if (SIZE_SOURCE == "table")
        {
            var tmap = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase); int n = 0;
            foreach (string raw in TABLE.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0) continue; n++;
                var parts = line.Split(new[] { ',', '\t', '，' }, 2);
                string key = parts[0].Trim(), val = parts.Length > 1 ? parts[1].Trim() : ""; string st;
                if (key == "" || val == "") st = "缺值";
                else { var sz = ParseSize(val); if (sz == null) st = "無法識別"; else if (tmap.ContainsKey(key)) st = Same(tmap[key], sz) ? "重複" : "矛盾"; else { tmap[key] = sz; st = "成功"; } }
                tableReport.Add("{\"row\":" + n + ",\"id\":" + J.Q(key) + ",\"size\":" + J.Q(val) + ",\"status\":" + J.Q(st) + "}");
            }
            for (int k = 0; k < loops.Count; k++) if (idOfCol[k] >= 0 && tmap.ContainsKey(ids[idOfCol[k]].T)) srcSize[k] = tmap[ids[idOfCol[k]].T];
        }

        // ---- 類型 ----
        var symbols = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var symByKey = new Dictionary<string, FamilySymbol>();
        foreach (var fs in symbols) { var z = ParseSize(fs.Name); if (z != null && !symByKey.ContainsKey(Key(z))) symByKey[Key(z)] = fs; }

        // ---- 預覽 ----
        var outRows = new List<Tuple<string, string>>(); var commit = new List<string>();
        var count = new SortedDictionary<string, int>(); var typeCount = new SortedDictionary<string, int>();
        for (int k = 0; k < loops.Count; k++)
        {
            var L = loops[k];
            string colId = idOfCol[k] >= 0 ? ids[idOfCol[k]].T : "";
            Size meas = L.Kind == "rect" ? new Size { Kind = "rect", W = R05(L.W * u / 10), H = R05(L.H * u / 10) }
                      : L.Kind == "circle" ? new Size { Kind = "circle", D = R05(L.D * u / 10) } : null;
            Size use = SIZE_SOURCE == "curve" ? meas : srcSize[k];
            string status, msg = ""; FamilySymbol fs = null;
            if (!L.Closed) { status = "排除"; msg = "輪廓未封閉"; }
            else if (meas == null) { status = "人工複核"; msg = "非矩形／圓形"; }
            else if (SIZE_SOURCE != "curve" && use == null) { status = colId == "" ? "無編號" : "無尺寸"; }
            else if (!Same(use, meas)) { status = "資料矛盾"; msg = Fmt(use) + " ≠ 量測 " + Fmt(meas); }
            else if (!symByKey.TryGetValue(Key(use), out fs)) { status = "無類型"; msg = "專案內沒有 " + TypeName(use); }
            else if (colId == "" && ID_LAYER != "") { status = "無編號"; }
            else status = "已配對";
            bool go = fs != null && (status == "已配對" || status == "無編號");
            var pr = toRvt(L.Ctr); double rot = th + (L.Kind == "rect" ? L.Ang : 0);
            count[status] = (count.ContainsKey(status) ? count[status] : 0) + 1;
            if (go)
            {
                typeCount[fs.Name] = (typeCount.ContainsKey(fs.Name) ? typeCount[fs.Name] : 0) + 1;
                commit.Add(fs.Id.Value + "|" + J.N(pr[0]) + "|" + J.N(pr[1]) + "|" + J.N(rot) + "|" + colId);
            }
            outRows.Add(Tuple.Create(colId, "{\"create\":" + (go ? "true" : "false") + ",\"id\":" + J.Q(colId)
                + ",\"measured\":" + J.Q(meas == null ? "" : Fmt(meas)) + ",\"source\":" + J.Q(SIZE_SOURCE == "curve" || use == null ? "" : Fmt(use))
                + ",\"type\":" + J.Q(fs == null ? "" : fs.Name) + ",\"status\":" + J.Q(status) + ",\"msg\":" + J.Q(msg) + "}"));
        }
        var sorted = outRows.OrderBy(r => r.Item1 == "" ? 1 : 0).ThenBy(r => Regex.Replace(r.Item1, @"\d+", m => m.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase);
        sb.Append(",\"loops\":").Append(loops.Count);
        sb.Append(",\"summary\":{").Append(string.Join(",", count.Select(kv => J.Q(kv.Key) + ":" + kv.Value))).Append("}");
        sb.Append(",\"toCreateByType\":{").Append(string.Join(",", typeCount.Select(kv => J.Q(kv.Key) + ":" + kv.Value))).Append("}");
        sb.Append(",\"unusedIdLabels\":[").Append(string.Join(",", ids.Where((l, i) => colOfId[i] < 0).Select(l => J.Q(l.T)))).Append("]");
        sb.Append(",\"tableParse\":[").Append(string.Join(",", tableReport)).Append("]");
        sb.Append(",\"rows\":[").Append(string.Join(",", sorted.Select(r => r.Item2))).Append("]");
        sb.Append(",\"commitRows\":[").Append(string.Join(",", commit.Select(J.Q))).Append("]}");
        return sb.ToString();
    }

    // ---------- CSV ----------
    static List<Row> ReadCsv(string path)
    {
        var res = new List<Row>();
        foreach (var line in File.ReadAllLines(path, Encoding.Default).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 11) continue;
            Func<string, double> d = t => { double v; return double.TryParse(t, NumberStyles.Float, IC, out v) ? v : 0; };
            res.Add(new Row { Layer = f[0], Type = f[1], Closed = f[3] == "1", X = d(f[4]), Y = d(f[5]), X2 = d(f[6]), Y2 = d(f[7]), R = d(f[8]), Text = f[9].Trim(), Verts = f[10] });
        }
        return res;
    }

    static List<Lbl> Labels(List<Row> rows, string layer)
    {
        if (string.IsNullOrEmpty(layer)) return new List<Lbl>();
        return rows.Where(r => r.Layer == layer && (r.Type == "TEXT" || r.Type == "MTEXT" || r.Type == "ATTRIB") && r.Text != "")
                   .Select(r => new Lbl { T = r.Text, P = new[] { r.X, r.Y } }).ToList();
    }

    // ---------- CAD 軸線 ----------
    static List<GLine> CadGrids(List<Row> rows, double u)
    {
        var lines = new List<GLine>();
        foreach (var r in rows.Where(r => r.Layer == GRID_LINE_LAYER))
        {
            if (r.Type == "LINE") lines.Add(new GLine { P = new[] { r.X, r.Y }, Q = new[] { r.X2, r.Y2 } });
            else if (r.Type == "LWPOLYLINE" && r.Verts != "")
            {
                var v = r.Verts.Split(';').Select(t => t.Split(' ').Select(x => double.Parse(x, IC)).ToArray()).ToList();
                if (v.Count >= 2) lines.Add(new GLine { P = v.First(), Q = v.Last() });
            }
        }
        // 只保留較長的線（軸線），排除圖塊內的短線
        if (lines.Count == 0) return lines;
        double maxLen = lines.Max(Len);
        lines = lines.Where(l => Len(l) > maxLen * 0.2).ToList();
        double perpTol = 500 / u, endTol = 3000 / u;
        foreach (var t in rows.Where(r => r.Layer == GRID_TEXT_LAYER && (r.Type == "TEXT" || r.Type == "MTEXT" || r.Type == "ATTRIB") && r.Text != ""))
        {
            GLine best = null; double bestScore = double.MaxValue;
            foreach (var l in lines)
            {
                double dx = l.Q[0] - l.P[0], dy = l.Q[1] - l.P[1], len = Math.Sqrt(dx * dx + dy * dy);
                double perp = Math.Abs((t.X - l.P[0]) * dy - (t.Y - l.P[1]) * dx) / len;
                double end = Math.Min(Dist(t.X, t.Y, l.P), Dist(t.X, t.Y, l.Q));
                if (perp <= perpTol && end <= endTol && perp + end < bestScore) { bestScore = perp + end; best = l; }
            }
            if (best != null && best.Name == null) best.Name = t.Text.Trim();
        }
        // 同名軸線（分段）只保留最長的一條
        return lines.Where(l => l.Name != null).GroupBy(l => l.Name).Select(g => g.OrderByDescending(Len).First())
                    .Concat(lines.Where(l => l.Name == null)).ToList();
    }

    static double[] Intersect(GLine a, GLine b)
    {
        double x1 = a.P[0], y1 = a.P[1], x2 = a.Q[0], y2 = a.Q[1], x3 = b.P[0], y3 = b.P[1], x4 = b.Q[0], y4 = b.Q[1];
        double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        double l1 = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)), l2 = Math.Sqrt((x4 - x3) * (x4 - x3) + (y4 - y3) * (y4 - y3));
        if (Math.Abs(den) < 0.2 * l1 * l2) return null;   // 夾角太小（近乎平行）
        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den;
        return new[] { x1 + t * (x2 - x1), y1 + t * (y2 - y1) };
    }

    // ---------- 柱線 ----------
    static List<Loop> BuildLoops(List<Row> rows)
    {
        var segs = new List<Seg>(); var loops = new List<Loop>();
        foreach (var r in rows)
        {
            if (r.Type == "LINE") segs.Add(new Seg { A = new[] { r.X, r.Y }, B = new[] { r.X2, r.Y2 } });
            else if (r.Type == "LWPOLYLINE" && r.Verts != "")
            {
                var v = r.Verts.Split(';').Select(t => t.Split(' ').Select(x => double.Parse(x, IC)).ToArray()).ToList();
                if (r.Closed) v.Add(v[0]);
                for (int i = 0; i + 1 < v.Count; i++) segs.Add(new Seg { A = v[i], B = v[i + 1] });
            }
            else if (r.Type == "CIRCLE") loops.Add(new Loop { Kind = "circle", Closed = true, Ctr = new[] { r.X, r.Y }, D = 2 * r.R, Segs = 1 });
            else if (r.Type == "ARC")
                segs.Add(new Seg { A = new[] { r.X + r.R * Math.Cos(r.X2), r.Y + r.R * Math.Sin(r.X2) }, B = new[] { r.X + r.R * Math.Cos(r.Y2), r.Y + r.R * Math.Sin(r.Y2) }, IsArc = true, C = new[] { r.X, r.Y }, R = r.R });
        }
        const double tol = 1e-3;   // 端點容差（CAD 單位；cm 圖＝0.01 mm）
        Func<double[], string> pk = p => Math.Round(p[0] / tol).ToString(IC) + "," + Math.Round(p[1] / tol).ToString(IC);
        var adj = new Dictionary<string, List<int>>();
        for (int i = 0; i < segs.Count; i++)
            foreach (var p in new[] { segs[i].A, segs[i].B }) { string k = pk(p); if (!adj.ContainsKey(k)) adj[k] = new List<int>(); adj[k].Add(i); }
        var seen = new bool[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            if (seen[i]) continue;
            var comp = new List<int>(); var st = new Stack<int>(); st.Push(i);
            while (st.Count > 0) { int j = st.Pop(); if (seen[j]) continue; seen[j] = true; comp.Add(j); foreach (var p in new[] { segs[j].A, segs[j].B }) foreach (int n in adj[pk(p)]) if (!seen[n]) st.Push(n); }
            var L = new Loop { Segs = comp.Count, Closed = comp.All(j => adj[pk(segs[j].A)].Count == 2 && adj[pk(segs[j].B)].Count == 2) };
            var pts = comp.SelectMany(j => new[] { segs[j].A, segs[j].B }).ToList();
            L.Ctr = new[] { (pts.Min(p => p[0]) + pts.Max(p => p[0])) / 2, (pts.Min(p => p[1]) + pts.Max(p => p[1])) / 2 };
            if (L.Closed && comp.Count == 4 && comp.All(j => !segs[j].IsArc))
            {
                var dirs = comp.Select(j => new[] { segs[j].B[0] - segs[j].A[0], segs[j].B[1] - segs[j].A[1] }).ToList();
                Func<double[], double> ln = d => Math.Sqrt(d[0] * d[0] + d[1] * d[1]);
                var u0 = new[] { dirs[0][0] / ln(dirs[0]), dirs[0][1] / ln(dirs[0]) };
                Func<double[], double> dot = d => Math.Abs((d[0] * u0[0] + d[1] * u0[1]) / ln(d));
                if (dirs.All(d => dot(d) < 1e-3 || dot(d) > 1 - 1e-3))
                {
                    if (Math.Abs(u0[0]) < Math.Abs(u0[1])) u0 = new[] { u0[1], -u0[0] };
                    if (u0[0] < 0) u0 = new[] { -u0[0], -u0[1] };
                    L.Kind = "rect"; L.Ang = Math.Atan2(u0[1], u0[0]);
                    L.W = dirs.Where(d => dot(d) > 0.5).Average(ln); L.H = dirs.Where(d => dot(d) <= 0.5).Average(ln);
                    L.Ctr = Mean(pts);
                }
            }
            else if (L.Closed && comp.All(j => segs[j].IsArc))
            {
                var c0 = segs[comp[0]];
                if (comp.All(j => Dist(segs[j].C[0], segs[j].C[1], c0.C) < tol * 10 && Math.Abs(segs[j].R - c0.R) < tol * 10)) { L.Kind = "circle"; L.Ctr = c0.C; L.D = 2 * c0.R; }
            }
            loops.Add(L);
        }
        return loops;
    }

    // ---------- 共用 ----------
    static int[] Pair(List<double[]> from, List<double[]> to, double max)
    {
        var cand = new List<Tuple<double, int, int>>();
        for (int i = 0; i < from.Count; i++) for (int j = 0; j < to.Count; j++) { double d = Dist(from[i][0], from[i][1], to[j]); if (d <= max) cand.Add(Tuple.Create(d, i, j)); }
        var res = Enumerable.Repeat(-1, from.Count).ToArray(); var used = new HashSet<int>();
        foreach (var c in cand.OrderBy(c => c.Item1)) if (res[c.Item2] < 0 && !used.Contains(c.Item3)) { res[c.Item2] = c.Item3; used.Add(c.Item3); }
        return res;
    }
    static readonly Regex RectRe = new Regex(@"(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)");
    static readonly Regex CircRe = new Regex(@"(?:^|[^A-Za-z])(?:C-)?[RDØøΦφ]\s*(\d+(?:\.\d+)?)");
    static Size ParseSize(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        double f = t.IndexOf("mm", StringComparison.OrdinalIgnoreCase) >= 0 ? 0.1 : 1;
        var m = RectRe.Match(t);
        if (m.Success) return new Size { Kind = "rect", W = R05(double.Parse(m.Groups[1].Value, IC) * f), H = R05(double.Parse(m.Groups[2].Value, IC) * f) };
        m = CircRe.Match(t);
        return m.Success ? new Size { Kind = "circle", D = R05(double.Parse(m.Groups[1].Value, IC) * f) } : null;
    }
    static bool Same(Size a, Size b) { return a != null && b != null && a.Kind == b.Kind && (a.Kind == "rect" ? Math.Abs(a.W - b.W) <= 0.5 && Math.Abs(a.H - b.H) <= 0.5 : Math.Abs(a.D - b.D) <= 0.5); }
    static string Key(Size z) { return z.Kind == "rect" ? "R:" + J.N(z.W) + "x" + J.N(z.H) : "C:" + J.N(z.D); }
    static string Fmt(Size z) { return z.Kind == "rect" ? J.N(z.W) + "x" + J.N(z.H) : "D" + J.N(z.D); }
    static string TypeName(Size z) { return z.Kind == "rect" ? "C" + Fmt(z) + "cm" : "C-R" + J.N(z.D) + "cm"; }
    static double R05(double v) { return Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2; }
    static double Len(GLine l) { return Dist(l.P[0], l.P[1], l.Q); }
    static double Dist(double x, double y, double[] p) { return Math.Sqrt((x - p[0]) * (x - p[0]) + (y - p[1]) * (y - p[1])); }
    static double[] Mean(List<double[]> ps) { return new[] { ps.Average(p => p[0]), ps.Average(p => p[1]) }; }
    static double[] P2(XYZ p) { return new[] { p.X, p.Y }; }
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
