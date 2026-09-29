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
        /// <summary>Load, apply an edit, save. The edit returns a JObject describing what changed.</summary>
        private static JToken Edit(JObject a, Func<E.ReportDocument, JObject> edit)
        {
            // Inside batch_edit the document is already open and is saved once at the end.
            if (_batchDoc != null) return edit(_batchDoc) ?? new JObject();
            return ReportIO.With((string)a["path"], rd =>
            {
                var result = edit(rd) ?? new JObject();
                result.Merge(ReportIO.Save(rd, a));
                return result;
            });
        }

        [ThreadStatic] private static E.ReportDocument _batchDoc;

        internal static readonly string[] EditTools =
        {
            "set_formula", "delete_formula", "set_selection_formula", "add_parameter", "delete_parameter",
            "set_command_sql", "set_datasource", "remove_table", "set_text", "set_object_props",
            "set_section_props", "add_text_object", "add_field_object", "delete_object",
            "set_field_format", "set_condition_formula", "add_line", "add_box", "add_picture", "replace_picture"
        };

        /// <summary>Runs several edit tools on one loaded document: one load, one backup, one save, all-or-nothing.</summary>
        private static JToken BatchEdit(JObject a)
        {
            if (!(a["operations"] is JArray ops) || ops.Count == 0) throw new ToolError("operations must be a non-empty array.");
            if (_batchDoc != null) throw new ToolError("batch_edit cannot be nested.");
            // Validate the whole list before opening the report.
            for (int i = 0; i < ops.Count; i++)
            {
                if (!(ops[i] is JObject o)) throw new ToolError($"Operation {i + 1} is not an object.");
                if (!EditTools.Contains((string)o["tool"]))
                    throw new ToolError($"Operation {i + 1}: '{(string)o["tool"]}' is not an edit tool. Allowed: {string.Join(", ", EditTools)}.");
            }

            return ReportIO.With((string)a["path"], rd =>
            {
                var results = new JArray();
                _batchDoc = rd;
                try
                {
                    for (int i = 0; i < ops.Count; i++)
                    {
                        var op = (JObject)ops[i];
                        var tool = (string)op["tool"];
                        var args = (JObject)op.DeepClone();
                        args.Remove("tool");
                        args.Remove("output_path");
                        args["path"] = a["path"];
                        try
                        {
                            results.Add(new JObject { ["op"] = i + 1, ["tool"] = tool, ["result"] = _registry.Invoke(tool, args) });
                        }
                        catch (Exception ex)
                        {
                            throw new ToolError($"Operation {i + 1} ({tool}) failed, nothing was saved: {ToolRegistry.Describe(ex)}");
                        }
                    }
                }
                finally { _batchDoc = null; }

                var res = new JObject { ["operations"] = results };
                res.Merge(ReportIO.Save(rd, a));
                return res;
            });
        }

        private static string Sub(JObject a) => string.IsNullOrWhiteSpace((string)a["subreport"]) ? null : (string)a["subreport"];

        private static E.ReportObject EngineObject(E.ReportDocument rd, string name)
        {
            foreach (E.ReportObject o in rd.ReportDefinition.ReportObjects)
                if (Eq(o.Name, name)) return o;
            throw new ToolError($"Object '{name}' not found. Run inspect_report to list object names.");
        }

        // ---------- formulas ----------

        private static JToken SetFormula(JObject a) => Edit(a, main =>
        {
            var name = Norm((string)a["name"], "@");
            var text = (string)a["text"] ?? "";
            var rd = ReportIO.Scope(main, Sub(a));
            var existing = rd.DataDefinition.FormulaFields.Cast<E.FormulaFieldDefinition>().FirstOrDefault(f => Eq(f.Name, name));
            if (existing != null)
            {
                var old = existing.Text;
                existing.Text = text;
                return new JObject { ["formula"] = name, ["action"] = "updated", ["old_text"] = old };
            }

            var ff = new DD.FormulaField
            {
                Name = name,
                Text = text,
                Syntax = Eq((string)a["syntax"], "basic") ? DD.CrFormulaSyntaxEnum.crFormulaSyntaxBasic : DD.CrFormulaSyntaxEnum.crFormulaSyntaxCrystal
            };
            Ras.For(main, Sub(a)).DataDef.FormulaFieldController.Add(ff);
            return new JObject { ["formula"] = name, ["action"] = "created" };
        });

        private static JToken DeleteFormula(JObject a) => Edit(a, main =>
        {
            var name = Norm((string)a["name"], "@");
            var ras = Ras.For(main, Sub(a));
            var f = ras.DataDef.DataDefinition.FormulaFields.Cast<DD.ISCRField>().FirstOrDefault(x => Eq(x.Name, name))
                    ?? throw new ToolError($"Formula '{name}' not found.");
            var removedObjects = GuardUsage(main, Sub(a), ras, f.FormulaForm, (bool?)a["force"] ?? false);
            ras.DataDef.FormulaFieldController.Remove(f);
            return new JObject { ["formula"] = name, ["action"] = "deleted", ["removed_objects"] = removedObjects };
        });

        /// <summary>
        /// Crystal silently deletes every object bound to a removed formula/parameter. Refuse when the field is
        /// still referenced unless force is set; with force, report which objects go with it.
        /// </summary>
        private static JArray GuardUsage(E.ReportDocument main, string sub, Ras ras, string formulaForm, bool force) =>
            GuardUsage(main, sub, ras, new[] { formulaForm }, formulaForm, force);

        /// <summary>
        /// Lists every place that references any of <paramref name="forms"/> (e.g. {@F}, {?P}, {Table.Col}):
        /// bound field objects, fields embedded in text objects, formulas, selection formulas, conditional
        /// (suppress/color/format) formulas, groups, sorts and SQL commands. Throws unless force is set.
        /// Returns the names of field objects bound directly to one of them.
        /// </summary>
        private static JArray GuardUsage(E.ReportDocument main, string sub, Ras ras, ICollection<string> forms, string label, bool force)
        {
            bool Uses(string text) => text != null && forms.Any(f => text.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            bool Is(string name) => forms.Any(f => Eq(f, name));

            var boundObjects = ras.Objects().OfType<RD.FieldObject>().Where(o => Is(o.DataSourceName)).Select(o => o.Name).ToList();
            var usages = boundObjects.Select(n => "field object " + n).ToList();

            var rd = ReportIO.Scope(main, sub);
            usages.AddRange(rd.ReportDefinition.ReportObjects.OfType<E.TextObject>()
                .Where(t => Uses(t.Text)).Select(t => "text object " + t.Name));

            var dd = rd.DataDefinition;
            usages.AddRange(dd.FormulaFields.Cast<E.FormulaFieldDefinition>()
                .Where(x => !Is(x.FormulaName) && Uses(x.Text)).Select(x => "formula " + x.FormulaName));
            if (Uses(dd.RecordSelectionFormula)) usages.Add("record selection");
            if (Uses(dd.GroupSelectionFormula)) usages.Add("group selection");
            usages.AddRange(dd.Groups.Cast<E.Group>().Where(g => Is(g.ConditionField?.FormulaName)).Select(g => "group on " + g.ConditionField.FormulaName));
            usages.AddRange(dd.SortFields.Cast<E.SortField>().Where(s => Is(s.Field?.FormulaName)).Select(s => "sort on " + s.Field.FormulaName));
            usages.AddRange(ras.Db.Database.Tables.Cast<DD.Table>().OfType<DD.CommandTable>()
                .Where(t => Uses(t.CommandText)).Select(t => "SQL command " + t.Alias));
            foreach (var item in ras.AllConditions())
                foreach (var c in item.Value.Where(c => Uses(c.Value)))
                    usages.Add($"condition {item.Key}.{c.Key}");

            if (usages.Count > 0 && !force)
                throw new ToolError($"{label} is still used by: {string.Join(", ", usages.Distinct())}. " +
                                    "Remove those usages first, or pass force=true (bound field objects are deleted with it; other references will break).");
            return new JArray(boundObjects);
        }

        private static JToken SetSelectionFormula(JObject a) => Edit(a, main =>
        {
            var dd = ReportIO.Scope(main, Sub(a)).DataDefinition;
            var formula = (string)a["formula"] ?? "";
            if (Eq((string)a["kind"], "group"))
            {
                var old = dd.GroupSelectionFormula;
                dd.GroupSelectionFormula = formula;
                return new JObject { ["group_selection"] = formula, ["old"] = old };
            }
            var oldRec = dd.RecordSelectionFormula;
            dd.RecordSelectionFormula = formula;
            return new JObject { ["record_selection"] = formula, ["old"] = oldRec };
        });

        // ---------- parameters ----------

        private static DD.CrFieldValueTypeEnum ValueType(string type)
        {
            switch ((type ?? "string").ToLowerInvariant())
            {
                case "string": return DD.CrFieldValueTypeEnum.crFieldValueTypeStringField;
                case "number": return DD.CrFieldValueTypeEnum.crFieldValueTypeNumberField;
                case "currency": return DD.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField;
                case "boolean": return DD.CrFieldValueTypeEnum.crFieldValueTypeBooleanField;
                case "date": return DD.CrFieldValueTypeEnum.crFieldValueTypeDateField;
                case "datetime": return DD.CrFieldValueTypeEnum.crFieldValueTypeDateTimeField;
                case "time": return DD.CrFieldValueTypeEnum.crFieldValueTypeTimeField;
                default: throw new ToolError("Unknown parameter type: " + type);
            }
        }

        private static object ConvertValue(string value, DD.CrFieldValueTypeEnum type)
        {
            switch (type)
            {
                case DD.CrFieldValueTypeEnum.crFieldValueTypeNumberField:
                case DD.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField:
                    return decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                case DD.CrFieldValueTypeEnum.crFieldValueTypeBooleanField:
                    return bool.Parse(value);
                case DD.CrFieldValueTypeEnum.crFieldValueTypeDateField:
                case DD.CrFieldValueTypeEnum.crFieldValueTypeDateTimeField:
                case DD.CrFieldValueTypeEnum.crFieldValueTypeTimeField:
                    return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return value;
            }
        }

        private static JToken AddParameter(JObject a) => Edit(a, main =>
        {
            var name = Norm((string)a["name"], "?");
            var type = ValueType((string)a["type"]);
            var ras = Ras.For(main, Sub(a));
            if (ras.DataDef.DataDefinition.ParameterFields.Cast<DD.ISCRField>().Any(p => Eq(p.Name, name)))
                throw new ToolError($"Parameter '{name}' already exists.");

            var pf = new DD.ParameterField
            {
                Name = name,
                Type = type,
                ParameterType = DD.CrParameterFieldTypeEnum.crParameterFieldTypeReportParameter,
                AllowMultiValue = (bool?)a["allow_multiple"] ?? false,
                AllowCustomCurrentValues = true,
                ValueRangeKind = DD.CrParameterValueRangeKindEnum.crParameterValueRangeKindDiscrete,
            };
            var prompt = (string)a["prompt"];
            if (!string.IsNullOrEmpty(prompt)) pf.Description = prompt;
            if (a["default_values"] is JArray defs && defs.Count > 0)
            {
                var values = new DD.Values();
                foreach (var d in defs)
                    values.Add(new DD.ParameterFieldDiscreteValue { Value = ConvertValue((string)d, type) });
                pf.DefaultValues = values;
            }
            ras.DataDef.ParameterFieldController.Add(pf);
            return new JObject { ["parameter"] = name, ["action"] = "created" };
        });

        private static JToken DeleteParameter(JObject a) => Edit(a, main =>
        {
            var name = Norm((string)a["name"], "?");
            var ras = Ras.For(main, Sub(a));
            var p = ras.DataDef.DataDefinition.ParameterFields.Cast<DD.ISCRField>().FirstOrDefault(x => Eq(x.Name, name))
                    ?? throw new ToolError($"Parameter '{name}' not found.");
            var removedObjects = GuardUsage(main, Sub(a), ras, p.FormulaForm, (bool?)a["force"] ?? false);
            ras.DataDef.ParameterFieldController.Remove(p);
            return new JObject { ["parameter"] = name, ["action"] = "deleted", ["removed_objects"] = removedObjects };
        });

        // ---------- database ----------

        private static JToken SetCommandSql(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var logon = ReportIO.GetLogon(a);
            ras.Logon(logon);
            var alias = (string)a["table"];
            var target = ras.Db.Database.Tables.Cast<DD.Table>()
                .OfType<DD.CommandTable>()
                .FirstOrDefault(t => string.IsNullOrWhiteSpace(alias) || Eq(t.Alias, alias));
            if (target == null)
            {
                var names = string.Join(", ", ras.Db.Database.Tables.Cast<DD.Table>().Select(t => $"{t.Alias} ({(t is DD.CommandTable ? "command" : "table")})"));
                throw new ToolError($"No SQL command table{(string.IsNullOrWhiteSpace(alias) ? "" : " '" + alias + "'")} found. Tables: {names}");
            }

            var old = target.CommandText;
            var nt = (DD.CommandTable)target.Clone(true);
            nt.CommandText = (string)a["sql"];
            ApplyCredentials(nt.ConnectionInfo, logon);
            ras.Db.SetTableLocation(target, nt);
            return new JObject { ["table"] = target.Alias, ["old_sql"] = old };
        });

        private static JToken RemoveTable(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var alias = (string)a["table"];
            var table = ras.Db.Database.Tables.Cast<DD.Table>().FirstOrDefault(t => Eq(t.Alias, alias))
                        ?? throw new ToolError($"Table '{alias}' not found.");
            bool force = (bool?)a["force"] ?? false;
            var forms = table.DataFields.Cast<DD.ISCRField>().Select(f => f.FormulaForm).ToList();
            var removed = GuardUsage(main, Sub(a), ras, forms, $"Table '{table.Alias}'", force);
            // Unlike formulas, Crystal refuses to drop a table while objects are bound to it, so delete them first.
            foreach (var name in removed.Values<string>())
                ras.ReportDef.ReportObjectController.Remove(ras.Object(name));
            ras.Db.RemoveTable(table.Alias);
            return new JObject { ["table"] = table.Alias, ["action"] = "removed", ["removed_objects"] = removed };
        });

        private static JToken SetDatasource(JObject a) => Edit(a, main =>
        {
            var logon = ReportIO.GetLogon(a);
            if (string.IsNullOrEmpty(logon.Server) && string.IsNullOrEmpty(logon.Database))
                throw new ToolError("Give server and/or database (directly or via connection).");
            var alias = (string)a["table"];
            var scopes = Sub(a) != null ? new List<string> { Sub(a) } : new List<string> { null }.Concat(Ras.SubreportNames(main)).ToList();

            var changed = new JArray();
            foreach (var scope in scopes)
            {
                var ras = Ras.For(main, scope);
                ras.Logon(logon);
                foreach (var t in ras.Db.Database.Tables.Cast<DD.Table>().ToList())
                {
                    if (!string.IsNullOrWhiteSpace(alias) && !Eq(t.Alias, alias)) continue;
                    var nt = (DD.Table)t.Clone(true);
                    var ci = nt.ConnectionInfo;
                    var attrs = ci.Attributes;
                    var lp = Prop2(attrs, "QE_LogonProperties") as DD.PropertyBag;
                    var oldDb = Prop(attrs, "QE_DatabaseName");

                    if (!string.IsNullOrEmpty(logon.Server))
                    {
                        attrs["QE_ServerDescription"] = logon.Server;
                        SetExisting(lp, logon.Server, "Data Source", "Server");
                    }
                    if (!string.IsNullOrEmpty(logon.Database))
                    {
                        attrs["QE_DatabaseName"] = logon.Database;
                        SetExisting(lp, logon.Database, "Initial Catalog", "Database");
                        // Qualified names look like OldDb.dbo.Table – point them at the new database.
                        if (!(nt is DD.CommandTable) && !string.IsNullOrEmpty(oldDb) && nt.QualifiedName != null &&
                            nt.QualifiedName.StartsWith(oldDb + ".", StringComparison.OrdinalIgnoreCase))
                            nt.QualifiedName = logon.Database + nt.QualifiedName.Substring(oldDb.Length);
                    }
                    var provider = (string)a["provider"];
                    if (!string.IsNullOrWhiteSpace(provider))
                    {
                        if (lp == null || !lp.PropertyIDs.Cast<string>().Any(k => Eq(k, "Provider")))
                            throw new ToolError($"Table '{t.Alias}' is not an OLE DB connection; 'provider' cannot be set.");
                        SetExisting(lp, provider, "Provider");
                    }
                    if (logon.IntegratedSpecified)
                    {
                        // Crystal stores this as the string "True"/"False".
                        SetExisting(lp, logon.Integrated ? "True" : "False", "Integrated Security");
                        if (logon.Integrated) { ci.UserName = ""; ci.Password = ""; }
                    }
                    ApplyCredentials(ci, logon);
                    nt.ConnectionInfo = ci;
                    ras.Db.SetTableLocation(t, nt);
                    changed.Add((scope == null ? "" : scope + "::") + t.Alias);
                }
            }
            return new JObject { ["tables"] = changed, ["server"] = logon.Server, ["database"] = logon.Database, ["provider"] = (string)a["provider"] };
        });

        private static void SetExisting(DD.PropertyBag bag, object value, params string[] keys)
        {
            if (bag == null) return;
            var ids = bag.PropertyIDs.Cast<string>().ToList();
            var hit = false;
            foreach (var k in keys)
            {
                var id = ids.FirstOrDefault(x => Eq(x, k));
                if (id != null) { bag[id] = value; hit = true; }
            }
            if (!hit) bag[keys[0]] = value;
        }

        private static void ApplyCredentials(DD.ConnectionInfo ci, ReportIO.Logon l)
        {
            if (ci == null || l == null || l.Integrated || string.IsNullOrEmpty(l.User)) return;
            ci.UserName = l.User;
            ci.Password = l.Password ?? "";
        }

        // ---------- layout: engine ----------

        private static JToken SetText(JObject a) => Edit(a, main =>
        {
            var rd = ReportIO.Scope(main, Sub(a));
            var obj = EngineObject(rd, (string)a["object"]);
            if (!(obj is E.TextObject t)) throw new ToolError($"'{obj.Name}' is a {obj.Kind}, not a text object.");
            var old = t.Text;
            t.Text = (string)a["text"] ?? "";
            return new JObject { ["object"] = obj.Name, ["old_text"] = old };
        });

        private static JToken SetObjectProps(JObject a) => Edit(a, main =>
        {
            var rd = ReportIO.Scope(main, Sub(a));
            var obj = EngineObject(rd, (string)a["object"]);
            var done = new JArray();

            if (a["left"] != null) { obj.Left = (int)a["left"]; done.Add("left"); }
            if (a["top"] != null) { obj.Top = (int)a["top"]; done.Add("top"); }
            if (a["width"] != null) { obj.Width = (int)a["width"]; done.Add("width"); }
            if (a["height"] != null) { obj.Height = (int)a["height"]; done.Add("height"); }

            bool fontChange = a["font_name"] != null || a["font_size"] != null || a["bold"] != null || a["italic"] != null || a["underline"] != null;
            if (fontChange || a["color"] != null)
            {
                Font current;
                switch (obj)
                {
                    case E.TextObject t: current = t.Font; break;
                    case E.FieldObject f: current = f.Font; break;
                    default: throw new ToolError($"Font/color can only be set on text or field objects ('{obj.Name}' is {obj.Kind}).");
                }
                if (fontChange)
                {
                    var style = current.Style;
                    style = Toggle(style, FontStyle.Bold, (bool?)a["bold"]);
                    style = Toggle(style, FontStyle.Italic, (bool?)a["italic"]);
                    style = Toggle(style, FontStyle.Underline, (bool?)a["underline"]);
                    var font = new Font((string)a["font_name"] ?? current.Name, (float?)a["font_size"] ?? current.SizeInPoints, style, GraphicsUnit.Point);
                    if (obj is E.TextObject t2) t2.ApplyFont(font); else ((E.FieldObject)obj).ApplyFont(font);
                    done.Add("font");
                }
                if (a["color"] != null)
                {
                    var color = ColorTranslator.FromHtml((string)a["color"]);
                    if (obj is E.TextObject t3) t3.Color = color; else ((E.FieldObject)obj).Color = color;
                    done.Add("color");
                }
            }

            var fmt = obj.ObjectFormat;
            if (a["align"] != null)
            {
                switch (((string)a["align"]).ToLowerInvariant())
                {
                    case "left": fmt.HorizontalAlignment = Alignment.LeftAlign; break;
                    case "center": fmt.HorizontalAlignment = Alignment.HorizontalCenterAlign; break;
                    case "right": fmt.HorizontalAlignment = Alignment.RightAlign; break;
                    case "justified": fmt.HorizontalAlignment = Alignment.Justified; break;
                    default: fmt.HorizontalAlignment = Alignment.DefaultAlign; break;
                }
                done.Add("align");
            }
            if (a["suppress"] != null) { fmt.EnableSuppress = (bool)a["suppress"]; done.Add("suppress"); }
            if (a["can_grow"] != null) { fmt.EnableCanGrow = (bool)a["can_grow"]; done.Add("can_grow"); }
            ApplyDrawingProps(obj, a, done);

            if (done.Count == 0) throw new ToolError("Nothing to change: pass at least one property.");
            return new JObject { ["object"] = obj.Name, ["changed"] = done, ["now"] = DescribeObject(obj) };
        });

        private static FontStyle Toggle(FontStyle style, FontStyle flag, bool? on) =>
            on == null ? style : on.Value ? style | flag : style & ~flag;

        private static JToken SetSectionProps(JObject a) => Edit(a, main =>
        {
            var rd = ReportIO.Scope(main, Sub(a));
            var name = (string)a["section"];
            var s = rd.ReportDefinition.Sections.Cast<E.Section>().FirstOrDefault(x => Eq(x.Name, name))
                    ?? throw new ToolError($"Section '{name}' not found. Sections: " +
                                           string.Join(", ", rd.ReportDefinition.Sections.Cast<E.Section>().Select(x => x.Name)));
            var done = new JArray();
            var f = s.SectionFormat;
            if (a["height"] != null) { s.Height = (int)a["height"]; done.Add("height"); }
            if (a["suppress"] != null) { f.EnableSuppress = (bool)a["suppress"]; done.Add("suppress"); }
            if (a["new_page_before"] != null) { f.EnableNewPageBefore = (bool)a["new_page_before"]; done.Add("new_page_before"); }
            if (a["new_page_after"] != null) { f.EnableNewPageAfter = (bool)a["new_page_after"]; done.Add("new_page_after"); }
            if (a["keep_together"] != null) { f.EnableKeepTogether = (bool)a["keep_together"]; done.Add("keep_together"); }
            if (a["background_color"] != null)
            {
                var c = (string)a["background_color"];
                f.BackgroundColor = string.IsNullOrWhiteSpace(c) ? Color.Empty : ColorTranslator.FromHtml(c);
                done.Add("background_color");
            }
            if (done.Count == 0) throw new ToolError("Nothing to change: pass at least one property.");
            return new JObject { ["section"] = s.Name, ["changed"] = done };
        });

        // ---------- layout: RAS (structural) ----------

        private static JToken AddTextObject(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);

            var element = new RD.ParagraphTextElement { Text = (string)a["text"] ?? "", Kind = RD.CrParagraphElementKindEnum.crParagraphElementKindText };
            var elements = new RD.ParagraphElements();
            elements.Add(element);
            var paragraphs = new RD.Paragraphs();
            paragraphs.Add(new RD.Paragraph { ParagraphElements = elements });

            var obj = new RD.TextObject { Paragraphs = paragraphs };
            Place(obj, a);
            return AddObject(ras, obj, section);
        });

        private static JToken AddFieldObject(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);
            var field = ras.Field((string)a["field"]);
            var obj = new RD.FieldObject { DataSourceName = field.FormulaForm, FieldValueType = field.Type };
            Place(obj, a);
            var res = AddObject(ras, obj, section);
            res["field"] = field.FormulaForm;
            return res;
        });

        private static void Place(RD.ISCRReportObject obj, JObject a)
        {
            obj.Left = (int)a["left"];
            obj.Top = (int)a["top"];
            obj.Width = (int)a["width"];
            obj.Height = (int)a["height"];
            var name = (string)a["name"];
            if (!string.IsNullOrWhiteSpace(name)) obj.Name = name;
        }

        private static JObject AddObject(Ras ras, RD.ISCRReportObject obj, RD.Section section)
        {
            var before = new HashSet<string>(ras.Objects().Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            ras.ReportDef.ReportObjectController.Add(obj, section, -1);
            var added = ras.Objects().Select(o => o.Name).Where(n => !before.Contains(n)).ToList();
            return new JObject { ["object"] = added.FirstOrDefault() ?? obj.Name, ["section"] = section.Name, ["action"] = "added" };
        }

        private static JToken DeleteObject(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var obj = ras.Object((string)a["object"]);
            ras.ReportDef.ReportObjectController.Remove(obj);
            return new JObject { ["object"] = obj.Name, ["action"] = "deleted" };
        });
    }
}
