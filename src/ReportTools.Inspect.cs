using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CrystalDecisions.Shared;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    internal static partial class ReportTools
    {
        private static JToken InspectReport(JObject a)
        {
            var sub = (string)a["subreport"];
            bool objects = (bool?)a["include_objects"] ?? true;
            bool fields = (bool?)a["include_fields"] ?? false;
            bool subs = (bool?)a["include_subreports"] ?? false;

            return ReportIO.With((string)a["path"], rd =>
            {
                var o = new JObject { ["path"] = ReportIO.ResolvePath((string)a["path"]) };
                if (!string.IsNullOrWhiteSpace(sub)) o["subreport"] = sub;
                o.Merge(Describe(rd, sub, objects, fields));
                if (subs && string.IsNullOrWhiteSpace(sub))
                {
                    var details = new JObject();
                    foreach (E.ReportDocument s in rd.Subreports)
                        details[s.Name] = Describe(rd, s.Name, objects, fields);
                    o["subreport_details"] = details;
                }
                return o;
            });
        }

        /// <summary>Describes the main report or one subreport. Collections are keyed by name so the output diffs cleanly.</summary>
        internal static JObject Describe(E.ReportDocument main, string sub, bool objects, bool fields)
        {
            var rd = ReportIO.Scope(main, sub);
            var o = new JObject();
            bool isMain = string.IsNullOrWhiteSpace(sub);

            if (isMain)
            {
                var si = rd.SummaryInfo;
                o["summary"] = Compact(new JObject
                {
                    ["title"] = si.ReportTitle, ["subject"] = si.ReportSubject,
                    ["author"] = si.ReportAuthor, ["comments"] = si.ReportComments
                });
                var po = rd.PrintOptions;
                var m = po.PageMargins;
                o["page"] = new JObject
                {
                    ["paper_size"] = po.PaperSize.ToString(),
                    ["orientation"] = po.PaperOrientation.ToString(),
                    ["printer"] = po.PrinterName,
                    ["margins_twips"] = new JObject { ["left"] = m.leftMargin, ["top"] = m.topMargin, ["right"] = m.rightMargin, ["bottom"] = m.bottomMargin }
                };
                o["subreports"] = new JArray(rd.Subreports.Cast<E.ReportDocument>().Select(s => s.Name));
            }

            o["tables"] = DescribeTables(main, sub, fields);
            o["links"] = DescribeLinks(main, sub);

            var dd = rd.DataDefinition;
            // IsLinked() throws on a subreport document; ask the main report, which lists subreport parameters too.
            var linked = isMain
                ? new HashSet<string>()
                : new HashSet<string>(main.DataDefinition.ParameterFields.Cast<E.ParameterFieldDefinition>()
                    .Where(p => Eq(p.ReportName, sub) && p.IsLinked()).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var parameters = new JObject();
            foreach (E.ParameterFieldDefinition p in dd.ParameterFields)
            {
                // The main report also lists subreport parameters; keep only this report's own ones.
                if (isMain && !string.IsNullOrEmpty(p.ReportName)) continue;
                var po = new JObject
                {
                    ["type"] = p.ParameterValueKind.ToString().Replace("Parameter", "").ToLowerInvariant(),
                    ["prompt"] = p.PromptText,
                    ["kind"] = p.DiscreteOrRangeKind.ToString(),
                    ["multiple"] = p.EnableAllowMultipleValue,
                };
                if (p.IsOptionalPrompt) po["optional"] = true;
                if (linked.Contains(p.Name)) po["linked"] = true;
                var defaults = new JArray();
                foreach (ParameterValue v in p.DefaultValues)
                    defaults.Add(v is ParameterDiscreteValue d ? ValueText(d.Value)
                        : v is ParameterRangeValue r ? $"{ValueText(r.StartValue)}..{ValueText(r.EndValue)}" : v.ToString());
                if (defaults.Count > 0) po["defaults"] = defaults;
                parameters[p.Name] = Compact(po);
            }
            o["parameters"] = parameters;

            o["formulas"] = new JObject(dd.FormulaFields.Cast<E.FormulaFieldDefinition>().Select(f => new JProperty(f.Name, f.Text ?? "")));
            var sqlExpr = new JObject(dd.SQLExpressionFields.Cast<E.SQLExpressionFieldDefinition>().Select(f => new JProperty(f.Name, f.Text ?? "")));
            if (sqlExpr.Count > 0) o["sql_expressions"] = sqlExpr;
            var running = new JObject(dd.RunningTotalFields.Cast<E.RunningTotalFieldDefinition>()
                .Select(f => new JProperty(f.Name, $"{f.Operation} of {f.SummarizedField?.FormulaName}")));
            if (running.Count > 0) o["running_totals"] = running;

            o["record_selection"] = dd.RecordSelectionFormula ?? "";
            o["group_selection"] = dd.GroupSelectionFormula ?? "";
            o["groups"] = new JArray(dd.Groups.Cast<E.Group>().Select(g => g.ConditionField?.FormulaName));
            o["sort"] = new JArray(dd.SortFields.Cast<E.SortField>().Select(s => $"{s.Field?.FormulaName} {s.SortDirection}"));

            // Conditional formulas are only reachable through RAS.
            var ras = Ras.For(main, sub);
            var conditions = ras.AllConditions();
            var formats = objects
                ? ras.Objects().OfType<RD.FieldObject>().ToDictionary(f => f.Name, f => FormatText(f.FieldFormat, f.FieldValueType.ToString()), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
            // Fields embedded in text objects carry their own format.
            var embedded = objects
                ? ras.Objects().OfType<RD.TextObject>()
                    .Select(t => new { t.Name, Fields = EmbeddedFields(t) }).Where(x => x.Fields.Count > 0)
                    .ToDictionary(x => x.Name, x => new JObject(x.Fields.Select(e => new JProperty(e.DataSource,
                        FormatText(e.FieldFormat, FieldTypeName(ras, e.DataSource)) ?? "default"))), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JObject>();

            var sections = new JObject();
            foreach (E.Section s in rd.ReportDefinition.Sections)
            {
                var so = new JObject { ["kind"] = s.Kind.ToString(), ["height"] = s.Height };
                var f = s.SectionFormat;
                if (f.EnableSuppress) so["suppress"] = true;
                if (f.EnableNewPageBefore) so["new_page_before"] = true;
                if (f.EnableNewPageAfter) so["new_page_after"] = true;
                if (conditions.TryGetValue(s.Name, out var sc)) so["conditions"] = JObject.FromObject(sc);
                if (objects)
                {
                    var objs = new JObject();
                    foreach (E.ReportObject ro in s.ReportObjects)
                    {
                        var d = DescribeObject(ro, s.Name);
                        if (formats.TryGetValue(ro.Name, out var fmt) && fmt != null) d["format"] = fmt;
                        if (embedded.TryGetValue(ro.Name, out var ef)) d["embedded_fields"] = ef;
                        if (conditions.TryGetValue(ro.Name, out var oc)) d["conditions"] = JObject.FromObject(oc);
                        objs[ro.Name] = d;
                    }
                    so["objects"] = objs;
                }
                else
                {
                    // Keep hidden logic visible even when objects are omitted.
                    var hidden = new JObject();
                    foreach (E.ReportObject ro in s.ReportObjects)
                        if (conditions.TryGetValue(ro.Name, out var oc)) hidden[ro.Name] = JObject.FromObject(oc);
                    if (hidden.Count > 0) so["object_conditions"] = hidden;
                }
                sections[s.Name] = so;
            }
            o["sections"] = sections;
            return o;
        }

        private static JObject DescribeObject(E.ReportObject ro, string section = null)
        {
            var o = new JObject
            {
                ["kind"] = ro.Kind.ToString().Replace("Object", ""),
                ["box"] = $"{ro.Left},{ro.Top} {ro.Width}x{ro.Height}"
            };
            switch (ro)
            {
                case E.TextObject t:
                    o["text"] = t.Text;
                    o["font"] = FontText(t.Font, t.Color);
                    break;
                case E.FieldObject f:
                    o["field"] = f.DataSource?.FormulaName;
                    o["font"] = FontText(f.Font, f.Color);
                    break;
                case E.SubreportObject s:
                    o["subreport"] = s.SubreportName;
                    break;
                case E.LineObject l:
                    o["box"] = $"{l.Left},{l.Top} -> {l.Right},{l.Bottom}";
                    if (section != null && !Eq(section, l.EndSectionName)) o["end_section"] = l.EndSectionName;
                    o["line"] = LineText(l);
                    break;
                case E.BoxObject b:
                    o["box"] = $"{b.Left},{b.Top} -> {b.Right},{b.Bottom}";
                    if (section != null && !Eq(section, b.EndSectionName)) o["end_section"] = b.EndSectionName;
                    o["line"] = LineText(b);
                    if (!b.FillColor.IsEmpty && b.FillColor.A != 0) o["fill"] = ColorTranslator.ToHtml(b.FillColor);
                    break;
            }
            var fmt = ro.ObjectFormat;
            if (fmt.EnableSuppress) o["suppress"] = true;
            if (fmt.EnableCanGrow) o["can_grow"] = true;
            if (fmt.HorizontalAlignment != Alignment.DefaultAlign && (ro is E.TextObject || ro is E.FieldObject))
                o["align"] = fmt.HorizontalAlignment.ToString();
            return o;
        }

        /// <summary>"single 20 #000000" style summary of a line/box border.</summary>
        private static string LineText(E.DrawingObject d)
        {
            var style = d.LineStyle.ToString().Replace("Line", "").ToLowerInvariant();
            if (style == "dash") style = "dashed";
            if (style == "dot") style = "dotted";
            if (style == "no") style = "none";
            return $"{style} {d.LineThickness} {ColorTranslator.ToHtml(d.LineColor)}";
        }

        private static string ValueText(object v) =>
            v is DateTime dt ? (dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss"))
            : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";

        private static string FontText(Font f, Color c)
        {
            if (f == null) return null;
            var s = $"{f.Name} {f.SizeInPoints:0.#}pt";
            if (f.Bold) s += " bold";
            if (f.Italic) s += " italic";
            if (f.Underline) s += " underline";
            if (c.ToArgb() != Color.Black.ToArgb()) s += " " + ColorTranslator.ToHtml(c);
            return s;
        }

        private static JObject DescribeTables(E.ReportDocument main, string sub, bool fields)
        {
            var res = new JObject();
            var db = Ras.For(main, sub).Db.Database;
            foreach (DD.Table t in db.Tables)
            {
                var o = new JObject { ["name"] = t.Name };
                if (!Eq(t.QualifiedName, t.Name)) o["qualified"] = t.QualifiedName;
                if (t is DD.CommandTable ct) { o["type"] = "command"; o["sql"] = ct.CommandText; }
                var ci = t.ConnectionInfo;
                if (ci != null)
                {
                    var attrs = ci.Attributes;
                    o["connection"] = Compact(new JObject
                    {
                        ["driver"] = Prop(attrs, "QE_DatabaseType"),
                        ["server"] = Prop(attrs, "QE_ServerDescription"),
                        ["database"] = Prop(attrs, "QE_DatabaseName"),
                        ["user"] = ci.UserName,
                        ["logon"] = LogonProps(attrs)
                    });
                }
                if (fields)
                    o["fields"] = new JArray(t.DataFields.Cast<DD.ISCRField>().Select(f => $"{f.Name}:{f.Type.ToString().Replace("crFieldValueType", "").Replace("Field", "")}"));
                res[t.Alias] = o;
            }
            return res;
        }

        private static JArray DescribeLinks(E.ReportDocument main, string sub)
        {
            var res = new JArray();
            foreach (DD.TableLink l in Ras.For(main, sub).Db.Database.TableLinks)
            {
                var src = l.SourceFieldNames.Cast<string>().Select(f => $"{l.SourceTableAlias}.{f}").ToList();
                var dst = l.TargetFieldNames.Cast<string>().Select(f => $"{l.TargetTableAlias}.{f}").ToList();
                res.Add($"{string.Join(",", src)} {l.JoinType.ToString().Replace("crTableJoinType", "")} {string.Join(",", dst)}");
            }
            return res;
        }

        private static string Prop(DD.PropertyBag bag, string key)
        {
            try { return bag?[key]?.ToString(); } catch { return null; }
        }

        private static JObject LogonProps(DD.PropertyBag attrs)
        {
            if (!(Prop2(attrs, "QE_LogonProperties") is DD.PropertyBag lp)) return null;
            var o = new JObject();
            foreach (string k in lp.PropertyIDs)
            {
                if (k.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                o[k] = lp[k]?.ToString();
            }
            return o.Count > 0 ? o : null;
        }

        private static object Prop2(DD.PropertyBag bag, string key)
        {
            try { return bag?[key]; } catch { return null; }
        }

        /// <summary>Drops null / empty values to keep output small.</summary>
        private static JObject Compact(JObject o)
        {
            foreach (var p in o.Properties().ToList())
                if (p.Value.Type == JTokenType.Null || (p.Value.Type == JTokenType.String && string.IsNullOrEmpty((string)p.Value)))
                    p.Remove();
            return o;
        }

        // ---------- diff ----------

        private static JToken DiffReports(JObject a)
        {
            JObject Snapshot(string path) => ReportIO.With(path, rd =>
            {
                var o = Describe(rd, null, true, true);
                var subs = new JObject();
                foreach (E.ReportDocument s in rd.Subreports) subs[s.Name] = Describe(rd, s.Name, true, true);
                o["subreport_details"] = subs;
                return o;
            });

            var left = Snapshot((string)a["path_a"]);
            var right = Snapshot((string)a["path_b"]);
            var changes = new JArray();
            Diff(left, right, "", changes);
            return new JObject { ["changes"] = changes.Count, ["diff"] = changes };
        }

        private static void Diff(JToken x, JToken y, string path, JArray output)
        {
            if (x is JObject ox && y is JObject oy)
            {
                foreach (var key in ox.Properties().Select(p => p.Name).Union(oy.Properties().Select(p => p.Name)))
                {
                    var p = path.Length == 0 ? key : path + "." + key;
                    var vx = ox[key];
                    var vy = oy[key];
                    if (vx == null) output.Add(new JObject { ["added"] = p, ["value"] = vy });
                    else if (vy == null) output.Add(new JObject { ["removed"] = p, ["value"] = vx });
                    else Diff(vx, vy, p, output);
                }
            }
            else if (!JToken.DeepEquals(x, y))
            {
                output.Add(new JObject { ["changed"] = path, ["from"] = x, ["to"] = y });
            }
        }
    }
}
