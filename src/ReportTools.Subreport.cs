using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>Inserting subreports and linking them to the main report.</summary>
    internal static partial class ReportTools
    {
        private static List<string> SubreportNames(E.ReportDocument main) =>
            main.ReportClientDocument.SubreportController.GetSubreportNames().Cast<string>().ToList();

        private static string ExistingSubreport(E.ReportDocument main, string name)
        {
            var names = SubreportNames(main);
            return names.FirstOrDefault(n => Eq(n, name))
                   ?? throw new ToolError($"Subreport '{name}' not found. Subreports: {(names.Count == 0 ? "none" : string.Join(", ", names))}.");
        }

        private static JToken AddSubreport(JObject a) => Edit(a, main =>
        {
            if (Sub(a) != null) throw new ToolError("Subreports cannot contain subreports; omit subreport.");
            var source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(((string)a["source_path"] ?? "").Trim().Trim('"')));
            if (!File.Exists(source) || !source.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) throw new ToolError("source_path must be an existing .rpt file: " + source);
            if (Eq(source, ReportIO.ResolvePath((string)a["path"]))) throw new ToolError("A report cannot import itself; copy it first and import the copy.");
            var name = string.IsNullOrWhiteSpace((string)a["name"]) ? Path.GetFileName(source) : ((string)a["name"]).Trim();
            if (SubreportNames(main).Any(n => Eq(n, name))) throw new ToolError($"There is already a subreport named '{name}'. Pass another name.");

            var ras = Ras.For(main, null);
            var section = ras.Section((string)a["section"]);
            int left = (int)a["left"], top = (int)a["top"], width = (int)a["width"], height = (int)a["height"];
            // Crystal refuses to save an object that does not fit its section ("Invalid section height").
            var engineSection = main.ReportDefinition.Sections.Cast<E.Section>().First(s => Eq(s.Name, section.Name));
            var grown = engineSection.Height < top + height;
            if (grown) engineSection.Height = top + height;

            var before = new HashSet<string>(ras.Objects().Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            try { main.ReportClientDocument.SubreportController.ImportSubreportEx(name, source, section, left, top, width, height); }
            catch (COMException ex) { throw new ToolError($"Crystal could not import {source}: {ex.Message.Trim()}"); }
            var obj = ras.Objects().Select(o => o.Name).FirstOrDefault(n => !before.Contains(n));

            var res = new JObject
            {
                ["subreport"] = name, ["object"] = obj, ["section"] = section.Name, ["action"] = "added",
                ["subreport_parameters"] = new JArray(Ras.For(main, name).DataDef.DataDefinition.ParameterFields.Cast<DD.ISCRField>().Select(p => p.FormulaForm)),
            };
            if (grown) res["section_height"] = top + height;
            if (a["links"] is JArray links && links.Count > 0) res["links"] = ApplyLinks(main, name, links, false);
            else if (((JArray)res["subreport_parameters"]).Count > 0)
                res["note"] = "The subreport's parameters will be prompted for unless linked: use set_subreport_links (e.g. {?Start} -> {?Start}).";
            return res;
        });

        private static JToken SetSubreportLinks(JObject a) => Edit(a, main =>
        {
            var name = ExistingSubreport(main, (string)a["subreport"]);
            if (!(a["links"] is JArray links)) throw new ToolError("links must be an array of {\"main\": ..., \"sub\": ...} ([] removes every link).");
            return new JObject { ["subreport"] = name, ["links"] = ApplyLinks(main, name, links, (bool?)a["replace"] ?? false) };
        });

        private static bool SamePair(string a1, string a2, string b1, string b2) =>
            (Eq(a1, b1) && Eq(a2, b2)) || (Eq(a1, b2) && Eq(a2, b1));

        private static JArray LinkListOf(DD.Database db) =>
            new JArray(db.TableLinks.Cast<DD.TableLink>()
                .Select(l => $"{l.SourceTableAlias} {l.JoinType} {l.TargetTableAlias}"));

        /// <summary>
        /// Changes the join type of a table link. An inner join drops the records of the main table
        /// that have no matching row in the joined one, so a report can come out empty even though
        /// the data is there; a left outer join keeps them.
        /// </summary>
        private static JToken SetTableLink(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var source = (string)a["source"];
            var target = (string)a["target"];
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                throw new ToolError("Give source and target as 'Table.Field'.");

            var srcFields = source.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            var dstFields = target.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            if (srcFields.Count != dstFields.Count)
                throw new ToolError("source and target must have the same number of fields.");
            if (!srcFields[0].Contains(".") || !dstFields[0].Contains("."))
                throw new ToolError("source and target must be 'Table.Field'.");
            var srcTable = srcFields[0].Split('.')[0];
            var dstTable = dstFields[0].Split('.')[0];

            var join = ((string)a["join"] ?? "").Trim().ToLowerInvariant();
            if (join == "none" || join == "remove")
            {
                // Drop the link between the two tables. The designer derives the links from the
                // joins, so both collections have to change, and the way they are stored matters:
                // TableLinks is rebuilt and assigned, TableJoins is emptied and refilled.
                var db0 = ras.Db.Database;
                var links0 = new DD.TableLinks();
                foreach (var l in db0.TableLinks.Cast<DD.TableLink>())
                    if (!SamePair(l.SourceTableAlias, l.TargetTableAlias, srcTable, dstTable))
                        links0.Add(l);
                db0.TableLinks = links0;
                var joins0 = db0.TableJoins;
                var keep = joins0.Cast<DD.TableJoin>()
                    .Where(j => !SamePair(j.SourceTableAlias, j.TargetTableAlias, srcTable, dstTable))
                    .Select(j => (DD.TableJoin)j.Clone(true)).ToList();
                joins0.RemoveAll();
                foreach (var j in keep) joins0.Add(j);
                return Compact(new JObject
                {
                    ["source"] = source, ["target"] = target, ["join"] = "none", ["removed"] = true,
                    ["links"] = LinkListOf(db0),
                });
            }
            var joinType = join switch
            {
                "inner" or "equal" => DD.CrTableJoinOperatorEnum.crTableJoinOperatorInnerJoin,
                "left" or "leftouter" or "left_outer" => DD.CrTableJoinOperatorEnum.crTableJoinOperatorLeftOuterJoin,
                "right" or "rightouter" or "right_outer" => DD.CrTableJoinOperatorEnum.crTableJoinOperatorRightOuterJoin,
                "outer" or "full" => DD.CrTableJoinOperatorEnum.crTableJoinOperatorFullOuterJoin,
                _ => throw new ToolError("join must be 'inner' or 'left'."),
            };

            // The designer keeps the join operators in TableJoins and derives the TableLinks from
            // them, so the operator has to be set in both. The collection has to be assigned back
            // as a whole: changing an item in place is discarded when the report is saved.
            var db = ras.Db.Database;
            var joins = db.TableJoins;
            var current = joins.Cast<DD.TableJoin>().FirstOrDefault(j =>
                Eq(j.SourceTableAlias, srcTable) && Eq(j.TargetTableAlias, dstTable));

            if (current == null)
            {
                var j = new DD.TableJoin
                {
                    SourceTableAlias = srcTable,
                    TargetTableAlias = dstTable,
                    JoinOperator = joinType,
                    EnforceJoin = DD.CrTableJoinEnforcedEnum.crTableJoinEnforcedBoth,
                };
                var fl = new DD.FieldLinks();
                for (int i = 0; i < srcFields.Count; i++)
                    fl.Add(new DD.FieldLink
                    {
                        FromField = Field(db, srcTable, srcFields[i]),
                        ToField = Field(db, dstTable, dstFields[i]),
                        LinkOperator = DD.CrFieldLinkOperatorEnum.crFieldLinkOperatorEqualLink,
                    });
                j.FieldLinks = fl;
                joins.Add(j);
            }
            else
            {
                current.JoinOperator = joinType;
                // The fields of an existing join have to be replaced too: the designer keeps them in
                // the join (and from there in the tables), so only the operator would change.
                var fl = new DD.FieldLinks();
                for (int i = 0; i < srcFields.Count; i++)
                    fl.Add(new DD.FieldLink
                    {
                        FromField = Field(db, srcTable, srcFields[i]),
                        ToField = Field(db, dstTable, dstFields[i]),
                        LinkOperator = DD.CrFieldLinkOperatorEnum.crFieldLinkOperatorEqualLink,
                    });
                current.FieldLinks = fl;
            }

            // Rebuild the links and assign the collection back, otherwise the change is lost on save.
            // Every link is cloned: re-adding an object that already belongs to the collection drops it.
            // The field names are stored without the table prefix; with it Crystal builds the
            // link as "ALIAS.ALIAS.FIELD".
            var links = new DD.TableLinks();
            foreach (var l in db.TableLinks.Cast<DD.TableLink>())
            {
                var copy = (DD.TableLink)l.Clone(true);
                if (Eq(l.SourceTableAlias, srcTable) && Eq(l.TargetTableAlias, dstTable))
                {
                    copy.JoinType = joinType == DD.CrTableJoinOperatorEnum.crTableJoinOperatorLeftOuterJoin
                        ? DD.CrTableJoinTypeEnum.crTableJoinTypeLeftOuterJoin
                        : DD.CrTableJoinTypeEnum.crTableJoinTypeEqualJoin;
                    copy.SourceFieldNames = Strings(srcFields.Select(Bare).ToList());
                    copy.TargetFieldNames = Strings(dstFields.Select(Bare).ToList());
                }
                links.Add(copy);
            }
            if (!links.Cast<DD.TableLink>().Any(l => Eq(l.SourceTableAlias, srcTable) && Eq(l.TargetTableAlias, dstTable)))
                links.Add(new DD.TableLink
                {
                    SourceTableAlias = srcTable,
                    TargetTableAlias = dstTable,
                    SourceFieldNames = Strings(srcFields.Select(Bare).ToList()),
                    TargetFieldNames = Strings(dstFields.Select(Bare).ToList()),
                    JoinType = joinType == DD.CrTableJoinOperatorEnum.crTableJoinOperatorLeftOuterJoin
                        ? DD.CrTableJoinTypeEnum.crTableJoinTypeLeftOuterJoin
                        : DD.CrTableJoinTypeEnum.crTableJoinTypeEqualJoin,
                });
            db.TableLinks = links;

            return Compact(new JObject
            {
                ["source"] = source,
                ["target"] = target,
                ["join"] = joinType.ToString().Replace("crTableJoinOperator", ""),
                ["links"] = ReportTools.DescribeLinks(main, Sub(a)),
                ["table_joins"] = new JArray(joins.Cast<DD.TableJoin>().Select(j => new JObject
                {
                    ["source"] = j.SourceTableAlias,
                    ["target"] = j.TargetTableAlias,
                    ["operator"] = j.JoinOperator.ToString().Replace("crTableJoinOperator", ""),
                })),
                ["table_links"] = new JArray(ras.Db.Database.TableLinks.Cast<DD.TableLink>().Select(l => new JObject
                {
                    ["source"] = l.SourceTableAlias,
                    ["target"] = l.TargetTableAlias,
                    ["type"] = l.JoinType.ToString().Replace("crTableJoinType", ""),
                })),
            });
        });

        /// <summary>Builds a RAS Strings collection out of the given names.</summary>
        private static DD.Strings Strings(List<string> names)
        {
            var s = new DD.StringsClass();
            foreach (var n in names) s.Add(n);
            return s;
        }

        /// <summary>The field name without the "Table." prefix.</summary>
        private static string Bare(string name) => name.Contains(".") ? name.Split('.').Last() : name;

        /// <summary>Finds a field of a table by name (e.g. "DocEntry"), for building a join.</summary>
        private static DD.ISCRField Field(DD.Database db, string alias, string name)
        {
            // the caller passes "Table.Field"; here only the field name is needed
            var field = name.Contains(".") ? name.Split('.').Last() : name;
            var table = db.Tables.Cast<DD.Table>().FirstOrDefault(t => Eq(t.Alias, alias))
                        ?? throw new ToolError($"No table with alias '{alias}'.");
            var f = table.DataFields.Cast<DD.ISCRField>().FirstOrDefault(x => Eq(x.Name, field))
                    ?? throw new ToolError($"Table '{alias}' has no field '{field}'.");
            return f;
        }

        private static bool SameFields(object names, List<string> wanted)
        {
            var list = (names as object[] ?? new object[0]).Select(o => Convert.ToString(o)).ToList();
            return list.Count == wanted.Count && !wanted.Where((w, i) => !Eq(list[i], w)).Any();
        }

        /// <summary>
        /// Adds (or with replace, sets) links. A link passes a main report value to the subreport:
        /// a main parameter to a subreport parameter ({?Start} -> {?Start}), or a main field/formula to a subreport field
        /// ({Orders.Customer} -> {Lines.Customer}). For a field link Crystal creates the parameter {?Pm-Orders.Customer}
        /// but, unlike the designer, does not filter the subreport by it, so the record selection is extended here.
        /// </summary>
        private static JArray ApplyLinks(E.ReportDocument main, string name, JArray requested, bool replace)
        {
            var controller = main.ReportClientDocument.SubreportController;
            var mainRas = Ras.For(main, null);
            var subRas = Ras.For(main, name);
            var subDoc = main.OpenSubreport(name);

            // SetSubreportLinks replaces every link, so start from the current ones.
            var current = controller.GetSubreportLinks(name).Cast<RD.SubreportLink>()
                .Select(l => (Main: l.MainReportFieldName, Sub: l.SubreportFieldName, Param: l.LinkedParameterName)).ToList();
            var removed = replace ? current.ToList() : new List<(string Main, string Sub, string Param)>();
            if (replace) current.Clear();
            var filters = new List<(string Field, string Param)>();
            var requestedFieldLinks = new List<(string Main, string Field)>();

            foreach (var item in requested)
            {
                if (!(item is JObject o) || string.IsNullOrWhiteSpace((string)o["main"]))
                    throw new ToolError("Each link is {\"main\": \"{?Param} or {Table.Field}\", \"sub\": \"{?Param} or {Table.Field}\"}.");
                var mainField = mainRas.Field((string)o["main"]);
                var mainForm = mainField.FormulaForm;
                // Default: the subreport field or parameter with the same name.
                var subField = subRas.Field(string.IsNullOrWhiteSpace((string)o["sub"]) ? mainForm : (string)o["sub"]);
                bool subIsParam = subField.Kind == DD.CrFieldKindEnum.crFieldKindParameterField;
                if (subIsParam && !SameType(mainField.Type, subField.Type))
                    throw new ToolError($"{mainForm} is {TypeText(mainField.Type)} but the subreport's {subField.FormulaForm} is {TypeText(subField.Type)}.");

                // Crystal's own naming for the parameter behind a field link. A link to a parameter
                // passes the value straight to it, so the parameter is the linked name itself; a link
                // to a field goes through a hidden parameter ("Pm-<field>") that Crystal creates.
                var param = subIsParam ? subField.FormulaForm : "{?Pm-" + Norm(mainForm, null) + "}";
                if (!subIsParam)
                {
                    filters.Add((subField.FormulaForm, param));
                    requestedFieldLinks.Add((mainForm, subField.FormulaForm));
                }
                current.RemoveAll(l => Eq(l.Sub, subField.FormulaForm));
                current.Add((mainForm, subField.FormulaForm, param));
            }

            var links = new RD.SubreportLinks();
            foreach (var l in current)
                links.Add(new RD.SubreportLink { MainReportFieldName = l.Main, SubreportFieldName = l.Sub, LinkedParameterName = l.Param });
            try { controller.SetSubreportLinks(name, links); }
            catch (COMException ex) { throw new ToolError($"Crystal refused the links: {ex.Message.Trim()}"); }

            // Crystal may rename the parameter behind a field link when the name is already taken
            // ("Pm-Orders.Customer??01"). The filter must use the name Crystal really used, or the
            // subreport reads a parameter that is never filled and returns no data.
            var actual = controller.GetSubreportLinks(name).Cast<RD.SubreportLink>()
                .Select(l => (Main: l.MainReportFieldName, Sub: l.SubreportFieldName, Param: l.LinkedParameterName)).ToList();
            for (int i = 0; i < filters.Count; i++)
            {
                var want = requestedFieldLinks[i];
                var a = actual.FirstOrDefault(x => Eq(x.Main, want.Main) && Eq(x.Sub, want.Field));
                if (!string.IsNullOrEmpty(a.Param) && !Eq(a.Param, filters[i].Param))
                    filters[i] = (filters[i].Field, a.Param);
            }

            // Filter the subreport by each new field link; drop the filters of field links that were removed.
            var dd = subDoc.DataDefinition;
            var selection = dd.RecordSelectionFormula ?? "";
            foreach (var r in removed.Where(r => !current.Any(c => Eq(c.Param, r.Param)) && !Eq(r.Sub, r.Param)
                                              && !string.IsNullOrEmpty(r.Param)))
                selection = WithoutClause(selection, $"{r.Sub} = {r.Param}");
            foreach (var (field, param) in filters)
            {
                var clause = $"{field} = {param}";
                if (selection.IndexOf(clause, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                selection = string.IsNullOrWhiteSpace(selection) ? clause : $"({selection}) and {clause}";
            }
            if (selection != (dd.RecordSelectionFormula ?? "")) dd.RecordSelectionFormula = selection;

            return LinkList(main, name);
        }

        /// <summary>
        /// Whether a main report field can feed a subreport parameter of another type. The numeric
        /// kinds are interchangeable (an int32 column can feed a "number" parameter), the rest are not.
        /// </summary>
        private static bool SameType(DD.CrFieldValueTypeEnum main, DD.CrFieldValueTypeEnum sub)
        {
            if (main == sub) return true;
            bool Numeric(DD.CrFieldValueTypeEnum t) =>
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeNumberField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeDecimalField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt16sField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt32sField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt64sField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt16uField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt32uField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt64uField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt8sField ||
                t == DD.CrFieldValueTypeEnum.crFieldValueTypeInt8uField;
            return Numeric(main) && Numeric(sub);
        }

        private static string WithoutClause(string selection, string clause)
        {
            if (Eq(selection.Trim(), clause)) return "";
            var suffix = ") and " + clause;
            var s = selection.Trim();
            return s.StartsWith("(") && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s.Substring(1, s.Length - 1 - suffix.Length) : selection;
        }

        internal static JArray LinkList(E.ReportDocument main, string name) =>
            new JArray(main.ReportClientDocument.SubreportController.GetSubreportLinks(name).Cast<RD.SubreportLink>()
                .Select(l => string.IsNullOrEmpty(l.LinkedParameterName) || Eq(l.SubreportFieldName, l.LinkedParameterName)
                    ? $"{l.MainReportFieldName} -> {l.SubreportFieldName}"
                    : $"{l.MainReportFieldName} -> {l.SubreportFieldName} (via {l.LinkedParameterName})"));
    }
}
