// revit_params.cs — 唯讀。列出結構柱可寫入的字串實例參數（Q8 選項來源）。不開 Transaction。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

public static class DwgColParams
{
    public static string Execute(Document doc)
    {
        var inst = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .WhereElementIsNotElementType().FirstElement();
        var names = new List<string>();
        string source;
        if (inst != null)
        {
            source = "existing instance " + inst.Id.Value;
            foreach (Parameter p in inst.Parameters)
                if (p.StorageType == StorageType.String && !p.IsReadOnly && p.Definition != null)
                    names.Add(p.Definition.Name);
        }
        else
        {
            // 專案中還沒有結構柱：列出內建參數與綁定到結構柱的專案／共用參數
            source = "no instance; built-in + bindings";
            var mark = LabelUtils.GetLabelFor(BuiltInParameter.ALL_MODEL_MARK);
            var comments = LabelUtils.GetLabelFor(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            names.Add(mark); names.Add(comments);
            var cat = Category.GetCategory(doc, BuiltInCategory.OST_StructuralColumns);
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                var b = it.Current as InstanceBinding;
                var def = it.Key as InternalDefinition;
                if (b == null || def == null || !b.Categories.Contains(cat)) continue;
                if (def.GetDataType() == SpecTypeId.String.Text) names.Add(def.Name);
            }
        }
        var mk = LabelUtils.GetLabelFor(BuiltInParameter.ALL_MODEL_MARK);
        var ordered = names.Distinct().OrderBy(n => n == mk ? 0 : 1).ThenBy(n => n).ToList();
        return "{\"source\":" + J.Q(source) + ",\"mark\":" + J.Q(mk) + ",\"params\":[" + string.Join(",", ordered.Select(J.Q)) + "]}";
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
