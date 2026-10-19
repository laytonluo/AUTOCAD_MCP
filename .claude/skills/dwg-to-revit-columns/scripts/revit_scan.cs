// revit_scan.cs — 唯讀。Q1 作業視圖、Q2 DWG 圖面、Q3 圖層清單的資料來源。不開 Transaction。
// Revit 2023 以前：new ElementId(xxxL) 改成 new ElementId((int)xxx)，.Value 改成 .IntegerValue。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

public static class DwgColScan
{
    // ===== 輸入 =====
    const string VIEW_KEY = "";     // 空白＝目前作用中視圖；或填視圖名稱／ElementId
    const string IMPORT_KEY = "";   // 空白＝只列出 DWG；或填 DWG 名稱／ElementId 以取得圖層
    // ================

    public static string Execute(Document doc)
    {
        var sb = new StringBuilder("{");
        View view = FindView(doc, VIEW_KEY);
        if (view == null) return "{\"error\":\"找不到視圖：" + (VIEW_KEY == "" ? "（目前作用中視圖）" : VIEW_KEY) + "\"}";

        Level lv = view.GenLevel;
        bool isPlan = (view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan) && lv != null;
        sb.Append("\"view\":{\"id\":").Append(view.Id.Value)
          .Append(",\"name\":").Append(J.Q(view.Name))
          .Append(",\"viewType\":").Append(J.Q(view.ViewType.ToString()))
          .Append(",\"isActive\":").Append(doc.ActiveView != null && doc.ActiveView.Id == view.Id ? "true" : "false")
          .Append(",\"isPlan\":").Append(isPlan ? "true" : "false");
        if (lv != null)
            sb.Append(",\"genLevel\":{\"id\":").Append(lv.Id.Value).Append(",\"name\":").Append(J.Q(lv.Name))
              .Append(",\"elevation_m\":").Append(J.N(lv.Elevation * 0.3048)).Append("}");
        sb.Append("}");

        // 視圖中的 DWG（Q2 選項）
        var imports = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        sb.Append(",\"importsInView\":[");
        sb.Append(string.Join(",", imports.Select(x =>
            "{\"id\":" + x.Id.Value + ",\"name\":" + J.Q(ImportName(doc, x)) + ",\"linked\":" + (x.IsLinked ? "true" : "false") + "}")));
        sb.Append("]");

        // 其他含 DWG 的平面視圖（Q1 選項 2～3）
        var otherPlans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.Id != view.Id && v.GenLevel != null
                        && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan))
            .Select(v => new { v, n = new FilteredElementCollector(doc, v.Id).OfClass(typeof(ImportInstance)).GetElementCount() })
            .Where(x => x.n > 0).OrderBy(x => x.v.Name).Take(5).ToList();
        sb.Append(",\"otherPlansWithDwg\":[");
        sb.Append(string.Join(",", otherPlans.Select(x =>
            "{\"id\":" + x.v.Id.Value + ",\"name\":" + J.Q(x.v.Name) + ",\"level\":" + J.Q(x.v.GenLevel.Name) + ",\"dwgCount\":" + x.n + "}")));
        sb.Append("]");

        if (IMPORT_KEY == "") return sb.Append("}").ToString();

        ImportInstance ii = imports.FirstOrDefault(x => x.Id.Value.ToString() == IMPORT_KEY.Trim()
                                                    || ImportName(doc, x) == IMPORT_KEY.Trim());
        if (ii == null)
        {
            long n;
            if (long.TryParse(IMPORT_KEY.Trim(), out n)) ii = doc.GetElement(new ElementId(n)) as ImportInstance;
        }
        if (ii == null) return sb.Append(",\"error\":").Append(J.Q("找不到 DWG：" + IMPORT_KEY)).Append("}").ToString();
        sb.Append(",\"import\":{\"id\":").Append(ii.Id.Value)
          .Append(",\"name\":").Append(J.Q(ImportName(doc, ii)))
          .Append(",\"linked\":").Append(ii.IsLinked ? "true" : "false")
          .Append(",\"inView\":").Append(imports.Any(x => x.Id == ii.Id) ? "true" : "false").Append("}");

        // 圖層統計（Q3 選項）
        var layers = new SortedDictionary<string, int[]>(); // [line, polyline, arc, circle]
        foreach (var g in DwgGeom.Collect(doc, ii))
        {
            int[] c;
            if (!layers.TryGetValue(g.Layer, out c)) { c = new int[4]; layers[g.Layer] = c; }
            if (g.Obj is Line) c[0]++;
            else if (g.Obj is PolyLine) c[1]++;
            else if (g.Obj is Arc) { if (((Arc)g.Obj).IsBound) c[2]++; else c[3]++; }
        }
        sb.Append(",\"layers\":[");
        sb.Append(string.Join(",", layers.Select(kv =>
            "{\"name\":" + J.Q(kv.Key) + ",\"line\":" + kv.Value[0] + ",\"polyline\":" + kv.Value[1] +
            ",\"arc\":" + kv.Value[2] + ",\"circle\":" + kv.Value[3] + "}")));
        sb.Append("]}");
        return sb.ToString();
    }

    static View FindView(Document doc, string key)
    {
        key = (key ?? "").Trim();
        if (key == "") return doc.ActiveView;
        long n;
        if (long.TryParse(key, out n))
        {
            var v = doc.GetElement(new ElementId(n)) as View;
            if (v != null) return v;
        }
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name == key);
    }

    static string ImportName(Document doc, ImportInstance x)
    {
        if (x.Category != null) return x.Category.Name;
        Element t = doc.GetElement(x.GetTypeId());
        return t != null ? t.Name : "";
    }
}

// ---- 共用：DWG 幾何收集（與 revit_analyze.cs 相同） ----
public class DwgGeomItem { public string Layer; public GeometryObject Obj; public Transform Tf; }

public static class DwgGeom
{
    // 回傳 DWG 本身座標（symbol space，單位 ft）的幾何；Tf 為巢狀圖塊的累積轉換
    public static List<DwgGeomItem> Collect(Document doc, ImportInstance ii)
    {
        var list = new List<DwgGeomItem>();
        GeometryElement ge = ii.get_Geometry(new Options());
        if (ge == null) return list;
        foreach (GeometryObject o in ge)
        {
            var gi = o as GeometryInstance;
            if (gi != null) Walk(doc, gi.GetSymbolGeometry(), Transform.Identity, list);
        }
        return list;
    }

    static void Walk(Document doc, GeometryElement ge, Transform tf, List<DwgGeomItem> list)
    {
        foreach (GeometryObject o in ge)
        {
            var gi = o as GeometryInstance;
            if (gi != null) { Walk(doc, gi.GetSymbolGeometry(), tf.Multiply(gi.Transform), list); continue; }
            var gs = doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
            string layer = (gs != null && gs.GraphicsStyleCategory != null) ? gs.GraphicsStyleCategory.Name : "(無圖層)";
            list.Add(new DwgGeomItem { Layer = layer, Obj = o, Tf = tf });
        }
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
    public static string N(double v) { return Math.Round(v, 4).ToString(System.Globalization.CultureInfo.InvariantCulture); }
}
