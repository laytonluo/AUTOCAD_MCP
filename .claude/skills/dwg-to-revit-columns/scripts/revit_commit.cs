// revit_commit.cs — 會修改模型。只有在 Q9 回答「建立」之後才可執行。
// 輸入來自 revit_analyze.cs 的 level.id 與 commitRows。
// Revit 2023 以前：new ElementId(xxxL) 改成 new ElementId((int)xxx)，.Id.Value 改成 .Id.IntegerValue。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

public static class DwgColCommit
{
    // ===== 輸入 =====
    const long LEVEL_ID = 0;
    // 「FamilySymbolId|X(ft)|Y(ft)|旋轉(rad)|柱編號」，直接貼上 commitRows
    static readonly string[] ROWS = {
    };
    const string MARK_PARAM = "";   // Q7 = 否 時留空；否則填 Q8 選定的參數名稱（例如「標記」或 "Mark"）
    // ================

    public static string Execute(Document doc)
    {
        var ic = CultureInfo.InvariantCulture;
        var level = doc.GetElement(new ElementId(LEVEL_ID)) as Level;
        if (level == null) return "{\"error\":\"找不到樓層 " + LEVEL_ID + "\"}";
        var results = new List<string>();
        int ok = 0, fail = 0;

        using (var t = new Transaction(doc, "DWG 建立結構柱"))
        {
            t.Start();
            for (int i = 0; i < ROWS.Length; i++)
            {
                var p = ROWS[i].Split('|');
                string mark = p.Length > 4 ? p[4] : "";
                try
                {
                    var sym = doc.GetElement(new ElementId(long.Parse(p[0], ic))) as FamilySymbol;
                    if (sym == null) throw new Exception("找不到 FamilySymbol " + p[0]);
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    var pt = new XYZ(double.Parse(p[1], ic), double.Parse(p[2], ic), level.ProjectElevation);
                    var fi = doc.Create.NewFamilyInstance(pt, sym, level, StructuralType.Column);
                    var off = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                    if (off != null && !off.IsReadOnly) off.Set(0.0);
                    double rot = double.Parse(p[3], ic);
                    if (Math.Abs(rot) > 1e-6)
                        ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), rot);
                    string note = "";
                    if (MARK_PARAM != "" && mark != "")
                    {
                        Parameter mp = fi.LookupParameter(MARK_PARAM);
                        if (mp == null && (MARK_PARAM == "Mark" || MARK_PARAM == "標記")) mp = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (mp == null || mp.IsReadOnly || mp.StorageType != StorageType.String) note = "參數無法寫入";
                        else mp.Set(mark);
                    }
                    ok++;
                    results.Add("{\"row\":" + (i + 1) + ",\"id\":" + J.Q(mark) + ",\"status\":\"成功\",\"elementId\":" + fi.Id.Value + ",\"note\":" + J.Q(note) + "}");
                }
                catch (Exception ex)
                {
                    fail++;
                    results.Add("{\"row\":" + (i + 1) + ",\"id\":" + J.Q(mark) + ",\"status\":\"失敗\",\"note\":" + J.Q(ex.Message) + "}");
                }
            }
            t.Commit();
        }
        return "{\"level\":" + J.Q(level.Name) + ",\"success\":" + ok + ",\"failed\":" + fail + ",\"rows\":[" + string.Join(",", results) + "]}";
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
}
