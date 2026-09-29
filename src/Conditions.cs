using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RptMcp
{
    /// <summary>
    /// Finds the conditional (x-2) formulas hidden in a RAS object's formatting: suppress, font color, border,
    /// number/date format, section new-page... They live in *ConditionFormulas collections spread over
    /// Format, FontColor, Border, FieldFormat.NumericFormat and so on, so the object graph is walked by reflection.
    /// </summary>
    internal static class Conditions
    {
        private const int MaxDepth = 4;

        /// <summary>Returns "Path.Kind" → formula text for every non-empty condition formula under <paramref name="root"/>.</summary>
        public static Dictionary<string, string> Scan(object root, Type declared)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Walk(root, declared, "", 0, result);
            return result;
        }

        /// <summary>
        /// COM objects often come back as System.__ComObject, which exposes no properties; then the declared
        /// (interface) type is used. Interfaces do not surface inherited members, so those are merged in.
        /// </summary>
        private static IEnumerable<PropertyInfo> PropertiesOf(object node, Type declared)
        {
            var t = node.GetType();
            if (t.IsCOMObject && t.FullName == "System.__ComObject") t = declared;
            var types = new[] { t }.Concat(t.GetInterfaces());
            return types.SelectMany(x => x.GetProperties()).GroupBy(p => p.Name).Select(g => g.First());
        }

        private static void Walk(object node, Type declared, string path, int depth, Dictionary<string, string> result)
        {
            if (node == null || depth > MaxDepth) return;
            foreach (var p in PropertiesOf(node, declared))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var t = p.PropertyType;
                if (t.IsPrimitive || t.IsEnum || t == typeof(string)) continue;
                if (!(t.Namespace ?? "").StartsWith("CrystalDecisions.ReportAppServer", StringComparison.Ordinal)) continue;
                if (p.Name == "Paragraphs" || p.Name == "ReportPartBookmark") continue;

                object value;
                try { value = p.GetValue(node, null); } catch { continue; }
                if (value == null) continue;

                var childPath = path.Length == 0 ? p.Name : path + "." + p.Name;
                if (t.Name.EndsWith("ConditionFormulas", StringComparison.Ordinal))
                    ReadCollection(value, t, p.Name == "ConditionFormulas" ? path : childPath, result);
                else
                    Walk(value, t, childPath, depth + 1, result);
            }
        }

        private static void ReadCollection(object collection, Type declared, string path, Dictionary<string, string> result)
        {
            var indexer = PropertiesOf(collection, declared).FirstOrDefault(p =>
            {
                var ps = p.GetIndexParameters();
                return ps.Length == 1 && ps[0].ParameterType.IsEnum;
            });
            if (indexer == null) return;
            var enumType = indexer.GetIndexParameters()[0].ParameterType;
            foreach (var kind in Enum.GetValues(enumType))
            {
                string text;
                try
                {
                    var formula = indexer.GetValue(collection, new[] { kind });
                    text = formula == null ? null
                        : PropertiesOf(formula, indexer.PropertyType).FirstOrDefault(p => p.Name == "Text")?.GetValue(formula, null) as string;
                }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(text))
                    result[path + "." + ShortName(kind.ToString())] = text;
            }
        }

        /// <summary>
        /// Sets (or clears, with empty text) the condition formula at <paramref name="path"/> on a cloned RAS object,
        /// using the same path names inspect_report prints, e.g. "Format.EnableSuppress", "FontColor.Color",
        /// "FieldFormat.NumericFormat.NDecimalPlaces". The caller commits the clone with ReportObjectController.Modify.
        /// </summary>
        public static string Set(object clone, Type declared, string path, string text)
        {
            var parts = path.Split('.');
            if (parts.Length < 2) throw new ToolError($"Condition path '{path}' must look like Format.EnableSuppress.");
            object node = clone;
            var nodeType = declared;
            foreach (var part in parts.Take(parts.Length - 1))
            {
                var p = PropertiesOf(node, nodeType).FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase) && x.GetIndexParameters().Length == 0)
                        ?? throw new ToolError($"'{part}' not found in condition path '{path}'. Use a path shown by inspect_report or listed in the tool description.");
                node = p.GetValue(node, null) ?? throw new ToolError($"'{part}' is not set on this object, so '{path}' cannot be used.");
                nodeType = p.PropertyType;
            }

            var collProp = PropertiesOf(node, nodeType).FirstOrDefault(p => p.Name == "ConditionFormulas")
                           ?? throw new ToolError($"'{string.Join(".", parts.Take(parts.Length - 1))}' has no condition formulas.");
            var coll = collProp.GetValue(node, null);
            var indexer = PropertiesOf(coll, collProp.PropertyType).First(p =>
            {
                var ps = p.GetIndexParameters();
                return ps.Length == 1 && ps[0].ParameterType.IsEnum;
            });
            var enumType = indexer.GetIndexParameters()[0].ParameterType;
            var kindName = parts[parts.Length - 1];
            var kind = Enum.GetValues(enumType).Cast<object>().FirstOrDefault(v => string.Equals(ShortName(v.ToString()), kindName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ToolError($"Unknown condition '{kindName}'. Valid here: {string.Join(", ", Enum.GetNames(enumType).Select(ShortName))}.");

            var formula = new CrystalDecisions.ReportAppServer.ReportDefModel.ConditionFormula
            {
                Text = text ?? "",
                Syntax = CrystalDecisions.ReportAppServer.DataDefModel.CrFormulaSyntaxEnum.crFormulaSyntaxCrystal
            };
            indexer.SetValue(coll, formula, new[] { kind });
            return string.Join(".", parts.Take(parts.Length - 1)) + "." + ShortName(kind.ToString());
        }

        /// <summary>crObjectFormatConditionFormulaTypeEnableSuppress → EnableSuppress.</summary>
        internal static string ShortName(string enumName)
        {
            var i = enumName.IndexOf("Type", StringComparison.Ordinal);
            return i >= 0 ? enumName.Substring(i + 4) : enumName;
        }
    }
}
