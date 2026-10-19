// revit_scan.cs — 唯讀。確認視圖與 ImportInstance，列出 DWG 圖層與曲線數。不開 Transaction。
// Revit 2023 以前：把 new ElementId(xxxL) 改成 new ElementId((int)xxx)，.Value 改成 .IntegerValue。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

public static class DwgColScan
{
    // ===== 輸入 =====
    const long VIEW_ID = 347452;
    const long IMPORT_ID = 351751;
    // ================

    public static string Execute(Document doc)
    {
        Element a = doc.GetElement(new ElementId(VIEW_ID));
        Element b = doc.GetElement(new ElementId(IMPORT_ID));
        bool swapped = false;
        if (a is ImportInstance && b is View) { Element t = a; a = b; b = t; swapped = true; }

        View view = a as View;
        ImportInstance ii = b as ImportInstance;
        var sb = new StringBuilder("{");
        sb.Append("\"swapped\":").Append(swapped ? "true" : "false");

        if (view == null) return sb.Append(",\"error\":\"找不到視圖 ").Append(VIEW_ID).Append("\"}").ToString();
        Level lv = view.GenLevel;
        bool isPlan = view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan
                      || view.ViewType == ViewType.CeilingPlan || view.ViewType == ViewType.AreaPlan;
        sb.Append(",\"view\":{\"id\":").Append(view.Id.Value)
          .Append(",\"name\":").Append(J.Q(view.Name))
          .Append(",\"viewType\":").Append(J.Q(view.ViewType.ToString()))
          .Append(",\"isPlan\":").Append(isPlan && lv != null ? "true" : "false");
        if (lv != null)
            sb.Append(",\"genLevel\":{\"id\":").Append(lv.Id.Value).Append(",\"name\":").Append(J.Q(lv.Name))
              .Append(",\"elevation_m\":").Append(J.N(lv.Elevation * 0.3048)).Append("}");
        sb.Append("}");

        // 視圖中所有 ImportInstance
        var imports = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        sb.Append(",\"importsInView\":[");
        sb.Append(string.Join(",", imports.Select(x =>
            "{\"id\":" + x.Id.Value + ",\"name\":" + J.Q(ImportName(doc, x)) + ",\"linked\":" + (x.IsLinked ? "true" : "false") + "}")));
        sb.Append("]");

        if (ii == null) return sb.Append(",\"error\":\"找不到 ImportInstance ").Append(IMPORT_ID).Append("\"}").ToString();
        bool inView = imports.Any(x => x.Id == ii.Id);
        sb.Append(",\"import\":{\"id\":").Append(ii.Id.Value)
          .Append(",\"name\":").Append(J.Q(ImportName(doc, ii)))
          .Append(",\"linked\":").Append(ii.IsLinked ? "true" : "false")
          .Append(",\"inView\":").Append(inView ? "true" : "false").Append("}");

        // 圖層統計（symbol geometry，含巢狀圖塊）
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
