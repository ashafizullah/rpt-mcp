using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>Groups, sorts, running totals, sections and moving objects between sections.</summary>
    internal static partial class ReportTools
    {
        // ---------- groups ----------

        private static List<DD.Group> Groups(Ras ras) => ras.DataDef.DataDefinition.Groups.Cast<DD.Group>().ToList();

        private static string GroupList(Ras ras)
        {
            var groups = Groups(ras);
            return groups.Count == 0 ? "none" : string.Join(", ", groups.Select((g, i) => $"{i}: {g.ConditionField.FormulaForm}"));
        }

        private static DD.Group FindGroup(Ras ras, string field, out int index)
        {
            var form = ras.Field(field).FormulaForm;
            var groups = Groups(ras);
            index = groups.FindIndex(g => Eq(g.ConditionField?.FormulaForm, form));
            if (index < 0) throw new ToolError($"The report is not grouped on {form}. Groups: {GroupList(ras)}.");
            return groups[index];
        }

        /// <summary>The header and footer sections of group <paramref name="index"/> (groups own the n-th group header/footer area).</summary>
        private static (RD.Area Header, RD.Area Footer) GroupAreas(Ras ras, int index)
        {
            var headers = ras.Areas().Where(x => x.Kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupHeader).ToList();
            // Footers are listed innermost first.
            var footers = ras.Areas().Where(x => x.Kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupFooter).Reverse().ToList();
            return (index < headers.Count ? headers[index] : null, index < footers.Count ? footers[index] : null);
        }

        private static bool IsDateLike(DD.CrFieldValueTypeEnum t) =>
            t == DD.CrFieldValueTypeEnum.crFieldValueTypeDateField || t == DD.CrFieldValueTypeEnum.crFieldValueTypeDateTimeField ||
            t == DD.CrFieldValueTypeEnum.crFieldValueTypeTimeField;

        private static readonly string[] DateConditions =
            { "daily", "weekly", "biweekly", "semimonthly", "monthly", "quarterly", "semiannually", "annually", "second", "minute", "hour", "ampm" };

        private static DD.CrDateConditionEnum DateCondition(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            var name = "crDateCondition" + (v == "ampm" ? "AmPm" : v.Length > 0 ? char.ToUpperInvariant(v[0]) + v.Substring(1) : "");
            if (!DateConditions.Contains(v) || !System.Enum.IsDefined(typeof(DD.CrDateConditionEnum), name))
                throw new ToolError($"Unknown date_condition '{value}'. Valid: {string.Join(", ", DateConditions)}.");
            return (DD.CrDateConditionEnum)System.Enum.Parse(typeof(DD.CrDateConditionEnum), name);
        }

        private static DD.CrSortDirectionEnum SortDirection(string value)
        {
            switch ((value ?? "ascending").Trim().ToLowerInvariant())
            {
                case "ascending": case "asc": return DD.CrSortDirectionEnum.crSortDirectionAscendingOrder;
                case "descending": case "desc": return DD.CrSortDirectionEnum.crSortDirectionDescendingOrder;
                default: throw new ToolError($"Unknown direction '{value}'. Valid: ascending, descending.");
            }
        }

        private static string DirectionText(DD.CrSortDirectionEnum d) =>
            d == DD.CrSortDirectionEnum.crSortDirectionDescendingOrder ? "descending"
            : d == DD.CrSortDirectionEnum.crSortDirectionAscendingOrder ? "ascending"
            : d.ToString().Replace("crSortDirection", "");

        private static JToken AddGroup(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var field = ras.Field((string)a["field"]);
            var groups = Groups(ras);
            var dup = groups.FindIndex(g => Eq(g.ConditionField?.FormulaForm, field.FormulaForm));
            if (dup >= 0)
                throw new ToolError($"The report is already grouped on {field.FormulaForm} (group {dup}). Groups: {GroupList(ras)}. Use delete_group first to rebuild it.");
            var gc = ras.DataDef.GroupController;
            if (!gc.CanGroupOn(field)) throw new ToolError($"Crystal cannot group on {field.FormulaForm} (e.g. memo/blob fields or formulas using summaries cannot be grouped).");

            bool dateLike = IsDateLike(field.Type);
            if (a["date_condition"] != null && !dateLike)
                throw new ToolError($"date_condition only applies to date, date-time and time fields; {field.FormulaForm} is {TypeText(field.Type)}.");
            var condition = a["date_condition"] != null ? DateCondition((string)a["date_condition"])
                : field.Type == DD.CrFieldValueTypeEnum.crFieldValueTypeTimeField ? DD.CrDateConditionEnum.crDateConditionSecond
                : DD.CrDateConditionEnum.crDateConditionDaily;

            var index = (int?)a["index"] ?? groups.Count;
            if (index < 0 || index > groups.Count) throw new ToolError($"index must be 0..{groups.Count} (0 = outermost; default {groups.Count} = innermost).");

            var group = new DD.Group
            {
                ConditionField = field,
                Options = dateLike ? new DD.DateGroupOptions { DateCondition = condition } : new DD.GroupOptions()
            };
            var before = new HashSet<string>(ras.SectionNames(), StringComparer.OrdinalIgnoreCase);
            try { gc.Add(index, group); }
            catch (COMException ex) { throw new ToolError($"Crystal could not group on {field.FormulaForm}: {ex.Message.Trim()}"); }

            // Groups sort their records; the group's sort sits at the same position in Sorts.
            var direction = SortDirection((string)a["direction"]);
            var sort = ras.DataDef.DataDefinition.Sorts.Cast<DD.ISCRSort>().ElementAtOrDefault(index);
            if (sort == null || !Eq(sort.SortField?.FormulaForm, field.FormulaForm)) sort = ras.DataDef.SortController.FindSort(field);
            if (sort != null && sort.Direction != direction) ras.DataDef.SortController.ModifySortDirection(sort, direction);

            var added = ras.SectionNames().Where(n => !before.Contains(n)).ToList();
            var (header, footer) = GroupAreas(ras, index);
            return Compact(new JObject
            {
                ["group"] = index,
                ["field"] = field.FormulaForm,
                ["date_condition"] = dateLike ? condition.ToString().Replace("crDateCondition", "").ToLowerInvariant() : null,
                ["direction"] = DirectionText(direction),
                ["group_header_section"] = header?.Sections.Cast<RD.Section>().FirstOrDefault()?.Name,
                ["group_footer_section"] = footer?.Sections.Cast<RD.Section>().FirstOrDefault()?.Name,
                ["sections_added"] = new JArray(added),
                ["groups"] = new JArray(Groups(ras).Select(g => g.ConditionField.FormulaForm)),
            });
        });

        private static JToken DeleteGroup(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var group = FindGroup(ras, (string)a["field"], out var index);
            var form = group.ConditionField.FormulaForm;
            bool force = (bool?)a["force"] ?? false;

            // Removing a group drops its header/footer sections and everything in them.
            var (header, footer) = GroupAreas(ras, index);
            var sectionNames = new[] { header, footer }.Where(x => x != null)
                .SelectMany(x => x.Sections.Cast<RD.Section>()).Select(s => s.Name).ToList();
            var objects = ras.Objects().Where(o => sectionNames.Any(s => Eq(s, o.SectionName))).Select(o => o.Name).ToList();
            var usages = objects.Select(o => "object " + o).ToList();

            // Summaries per group, e.g. Sum({T.Qty}, {T.Shift}), stop compiling without the group.
            var perGroup = new System.Text.RegularExpressions.Regex(@",\s*" + System.Text.RegularExpressions.Regex.Escape(form) + @"\s*[,)]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var rd = ReportIO.Scope(main, Sub(a));
            usages.AddRange(rd.DataDefinition.FormulaFields.Cast<E.FormulaFieldDefinition>().Where(f => f.Text != null && perGroup.IsMatch(f.Text)).Select(f => "formula " + f.FormulaName));
            if (perGroup.IsMatch(rd.DataDefinition.GroupSelectionFormula ?? "")) usages.Add("group selection");
            foreach (DD.RunningTotalField rt in ras.DataDef.DataDefinition.RunningTotalFields)
                if ((rt.ResetConditionType == DD.CrRunningTotalConditionEnum.crRunningTotalConditionOnChangeOfGroup && Eq(ConditionText(rt.ResetCondition), form)) ||
                    (rt.EvaluateConditionType == DD.CrRunningTotalConditionEnum.crRunningTotalConditionOnChangeOfGroup && Eq(ConditionText(rt.EvaluateCondition), form)))
                    usages.Add("running total " + rt.Name);

            if (usages.Count > 0 && !force)
                throw new ToolError($"Group {index} on {form} is still used by: {string.Join(", ", usages)}. " +
                                    "Remove or move those first, or pass force=true (objects in its sections are deleted; formulas that summarize per group will break).");
            foreach (var name in objects) ras.ReportDef.ReportObjectController.Remove(ras.Object(name));
            ras.DataDef.GroupController.Remove(group);
            return new JObject
            {
                ["group"] = index, ["field"] = form, ["action"] = "deleted",
                ["sections_removed"] = new JArray(sectionNames), ["removed_objects"] = new JArray(objects),
                ["groups"] = new JArray(Groups(ras).Select(g => g.ConditionField.FormulaForm)),
            };
        });

        // ---------- sorts ----------

        private static JToken AddSort(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var field = ras.Field((string)a["field"]);
            var sc = ras.DataDef.SortController;
            var direction = SortDirection((string)a["direction"]);
            int groupCount = Groups(ras).Count;
            var sorts = ras.DataDef.DataDefinition.Sorts.Cast<DD.ISCRSort>().ToList();

            var pos = sorts.FindIndex(s => Eq(s.SortField?.FormulaForm, field.FormulaForm));
            if (pos >= 0)
            {
                // Already sorted (as a record sort or by a group): change the direction instead of adding a duplicate.
                if (a["index"] != null) throw new ToolError($"{field.FormulaForm} is already sorted; delete_sort it first to move it.");
                var old = sorts[pos].Direction;
                if (old != direction) sc.ModifySortDirection(sorts[pos], direction);
                return new JObject
                {
                    ["field"] = field.FormulaForm, ["direction"] = DirectionText(direction), ["action"] = old == direction ? "unchanged" : "updated",
                    ["group_sort"] = pos < groupCount, ["sort"] = SortList(ras)
                };
            }
            if (!sc.CanSortOn(field)) throw new ToolError($"Crystal cannot sort on {field.FormulaForm}.");

            // index counts record sorts only; group sorts always come first.
            int recordSorts = sorts.Count - groupCount;
            var index = (int?)a["index"] ?? recordSorts;
            if (index < 0 || index > recordSorts) throw new ToolError($"index must be 0..{recordSorts} (position among the record sorts, after the {groupCount} group sort(s)).");
            sc.Add(groupCount + index, new DD.Sort { SortField = field, Direction = direction });
            return new JObject { ["field"] = field.FormulaForm, ["direction"] = DirectionText(direction), ["index"] = index, ["action"] = "added", ["sort"] = SortList(ras) };
        });

        private static JToken DeleteSort(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var field = ras.Field((string)a["field"]);
            var sorts = ras.DataDef.DataDefinition.Sorts.Cast<DD.ISCRSort>().ToList();
            var pos = sorts.FindIndex(s => Eq(s.SortField?.FormulaForm, field.FormulaForm));
            if (pos < 0) throw new ToolError($"There is no sort on {field.FormulaForm}. Sorts: {SortList(ras)}.");
            if (pos < Groups(ras).Count) throw new ToolError($"The sort on {field.FormulaForm} belongs to a group; use delete_group, or add_sort to change its direction.");
            ras.DataDef.SortController.Remove(sorts[pos]);
            return new JObject { ["field"] = field.FormulaForm, ["action"] = "deleted", ["sort"] = SortList(ras) };
        });

        private static JArray SortList(Ras ras) =>
            new JArray(ras.DataDef.DataDefinition.Sorts.Cast<DD.ISCRSort>().Select(s => $"{s.SortField?.FormulaForm} {DirectionText(s.Direction)}"));

        // ---------- running totals ----------

        private static readonly Dictionary<string, DD.CrSummaryOperationEnum> Operations = new Dictionary<string, DD.CrSummaryOperationEnum>(StringComparer.OrdinalIgnoreCase)
        {
            ["sum"] = DD.CrSummaryOperationEnum.crSummaryOperationSum,
            ["count"] = DD.CrSummaryOperationEnum.crSummaryOperationCount,
            ["distinct_count"] = DD.CrSummaryOperationEnum.crSummaryOperationDistinctCount,
            ["average"] = DD.CrSummaryOperationEnum.crSummaryOperationAverage,
            ["min"] = DD.CrSummaryOperationEnum.crSummaryOperationMinimum,
            ["max"] = DD.CrSummaryOperationEnum.crSummaryOperationMaximum,
        };

        private static readonly string[] Evaluations = { "each_record", "on_change_of_field", "on_change_of_group", "on_formula" };
        private static readonly string[] Resets = { "never", "on_change_of_field", "on_change_of_group", "on_formula" };

        /// <summary>Resolves evaluate/reset + its _on argument into the RAS condition type and value.</summary>
        private static (DD.CrRunningTotalConditionEnum Type, object Value, string Text) RunningCondition(Ras ras, string kind, string on, string arg)
        {
            switch (kind)
            {
                case "each_record":
                case "never":
                    if (!string.IsNullOrWhiteSpace(on)) throw new ToolError($"{arg}_on is only used with on_change_of_field, on_change_of_group or on_formula.");
                    return (DD.CrRunningTotalConditionEnum.crRunningTotalConditionNoCondition, null, kind);
                case "on_change_of_field":
                {
                    if (string.IsNullOrWhiteSpace(on)) throw new ToolError($"{arg}_on must name the field, e.g. {{Orders.Customer}}.");
                    var f = ras.Field(on);
                    return (DD.CrRunningTotalConditionEnum.crRunningTotalConditionOnChangeOfField, f, $"{kind} {f.FormulaForm}");
                }
                case "on_change_of_group":
                {
                    if (string.IsNullOrWhiteSpace(on)) throw new ToolError($"{arg}_on must name the field the group is on, e.g. {{Orders.Customer}}. Groups: {GroupList(ras)}.");
                    var g = FindGroup(ras, on, out _);
                    return (DD.CrRunningTotalConditionEnum.crRunningTotalConditionOnChangeOfGroup, g, $"{kind} {g.ConditionField.FormulaForm}");
                }
                case "on_formula":
                    if (string.IsNullOrWhiteSpace(on)) throw new ToolError($"{arg}_on must be a Boolean formula, e.g. {{Orders.Qty}} > 0.");
                    return (DD.CrRunningTotalConditionEnum.crRunningTotalConditionOnFormula, on, $"{kind} {on}");
                default:
                    throw new ToolError($"Unknown {arg} '{kind}'. Valid: {string.Join(", ", arg == "evaluate" ? Evaluations : Resets)}.");
            }
        }

        private static JToken AddRunningTotal(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var name = Norm((string)a["name"], "#");
            if (string.IsNullOrWhiteSpace(name)) throw new ToolError("name is required.");
            if (ras.DataDef.DataDefinition.RunningTotalFields.Cast<DD.ISCRField>().Any(x => Eq(x.Name, name)))
                throw new ToolError($"Running total '{name}' already exists.");
            var field = ras.Field((string)a["field"]);
            var opName = (string)a["operation"] ?? "sum";
            if (!Operations.TryGetValue(opName, out var op)) throw new ToolError($"Unknown operation '{opName}'. Valid: {string.Join(", ", Operations.Keys)}.");
            bool numeric = field.Type == DD.CrFieldValueTypeEnum.crFieldValueTypeNumberField || field.Type == DD.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField ||
                           field.Type.ToString().Contains("Int");
            if ((op == DD.CrSummaryOperationEnum.crSummaryOperationSum || op == DD.CrSummaryOperationEnum.crSummaryOperationAverage) && !numeric)
                throw new ToolError($"{opName} needs a number or currency field; {field.FormulaForm} is {TypeText(field.Type)}. Use count or distinct_count.");

            var evaluate = RunningCondition(ras, ((string)a["evaluate"] ?? "each_record").ToLowerInvariant(), (string)a["evaluate_on"], "evaluate");
            var reset = RunningCondition(ras, ((string)a["reset"] ?? "never").ToLowerInvariant(), (string)a["reset_on"], "reset");

            var counting = op == DD.CrSummaryOperationEnum.crSummaryOperationCount || op == DD.CrSummaryOperationEnum.crSummaryOperationDistinctCount;
            var rt = new DD.RunningTotalField
            {
                Name = name,
                Operation = op,
                SummarizedField = field,
                Type = counting ? DD.CrFieldValueTypeEnum.crFieldValueTypeNumberField : field.Type,
                EvaluateConditionType = evaluate.Type,
                ResetConditionType = reset.Type,
            };
            if (evaluate.Value != null) rt.EvaluateCondition = evaluate.Value;
            if (reset.Value != null) rt.ResetCondition = reset.Value;
            try { ras.RunningTotals("Add", rt); }
            catch (COMException ex) { throw new ToolError($"Crystal could not create the running total: {ex.Message.Trim()} (check the evaluate_on/reset_on formula)."); }

            return new JObject
            {
                ["running_total"] = "{#" + name + "}",
                ["field"] = field.FormulaForm,
                ["operation"] = opName.ToLowerInvariant(),
                ["evaluate"] = evaluate.Text,
                ["reset"] = reset.Text,
                ["note"] = $"Show it with add_field_object field {{#{name}}} (it is bound through a formula {{@{name}}}), or use {{#{name}}} in formulas.",
            };
        });

        private static JToken DeleteRunningTotal(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var name = Norm((string)a["name"], "#");
            var rt = ras.DataDef.DataDefinition.RunningTotalFields.Cast<DD.ISCRField>().FirstOrDefault(x => Eq(x.Name, name))
                     ?? throw new ToolError($"Running total '{name}' not found.");
            // Formulas that only show it ({@Name} = {#Name}, made by add_field_object) go with it.
            var wrappers = ras.DataDef.DataDefinition.FormulaFields.Cast<DD.ISCRField>().OfType<DD.FormulaField>()
                .Where(f => Eq(f.Text?.Trim(), rt.FormulaForm)).ToList();
            var forms = new[] { rt.FormulaForm }.Concat(wrappers.Select(w => w.FormulaForm)).ToList();
            var removed = GuardUsage(main, Sub(a), ras, forms, rt.FormulaForm, (bool?)a["force"] ?? false);
            foreach (var objName in removed.Values<string>())
                ras.ReportDef.ReportObjectController.Remove(ras.Object(objName));
            foreach (var w in wrappers) ras.DataDef.FormulaFieldController.Remove(w);
            ras.RunningTotals("Remove", rt);
            return new JObject
            {
                ["running_total"] = rt.FormulaForm, ["action"] = "deleted", ["removed_objects"] = removed,
                ["removed_formulas"] = new JArray(wrappers.Select(w => w.FormulaForm)),
            };
        });

        // ---------- sections ----------

        private static readonly Dictionary<string, RD.CrAreaSectionKindEnum> SectionKinds = new Dictionary<string, RD.CrAreaSectionKindEnum>(StringComparer.OrdinalIgnoreCase)
        {
            ["report_header"] = RD.CrAreaSectionKindEnum.crAreaSectionKindReportHeader,
            ["page_header"] = RD.CrAreaSectionKindEnum.crAreaSectionKindPageHeader,
            ["group_header"] = RD.CrAreaSectionKindEnum.crAreaSectionKindGroupHeader,
            ["detail"] = RD.CrAreaSectionKindEnum.crAreaSectionKindDetail,
            ["group_footer"] = RD.CrAreaSectionKindEnum.crAreaSectionKindGroupFooter,
            ["report_footer"] = RD.CrAreaSectionKindEnum.crAreaSectionKindReportFooter,
            ["page_footer"] = RD.CrAreaSectionKindEnum.crAreaSectionKindPageFooter,
        };

        private static string AreaText(RD.Area x) =>
            $"{x.Name} ({x.Kind.ToString().Replace("crAreaSectionKind", "")}: {string.Join(", ", x.Sections.Cast<RD.Section>().Select(s => s.Name))})";

        private static JToken AddSection(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var areas = ras.Areas().ToList();
            RD.Area area;
            var areaName = (string)a["area"];
            if (!string.IsNullOrWhiteSpace(areaName))
            {
                // Also accept a section name: the new section goes into that section's area.
                area = areas.FirstOrDefault(x => Eq(x.Name, areaName))
                       ?? areas.FirstOrDefault(x => x.Sections.Cast<RD.Section>().Any(s => Eq(s.Name, areaName)))
                       ?? throw new ToolError($"Area '{areaName}' not found. Areas: {string.Join("; ", areas.Select(AreaText))}.");
            }
            else
            {
                var kindName = (string)a["kind"];
                if (string.IsNullOrWhiteSpace(kindName)) throw new ToolError("Give kind (e.g. detail) or area.");
                if (!SectionKinds.TryGetValue(kindName, out var kind)) throw new ToolError($"Unknown kind '{kindName}'. Valid: {string.Join(", ", SectionKinds.Keys)}.");
                var matches = areas.Where(x => x.Kind == kind).ToList();
                if (matches.Count == 0)
                    throw new ToolError(kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupHeader || kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupFooter
                        ? "The report has no groups; create one with add_group (it adds the group header and footer)."
                        : $"The report has no {kindName} area.");
                if (matches.Count > 1)
                    throw new ToolError($"There are {matches.Count} {kindName} areas; pass area instead: {string.Join("; ", matches.Select(AreaText))}.");
                area = matches[0];
            }

            var count = area.Sections.Count;
            var index = (int?)a["index"] ?? count;
            if (index < 0 || index > count) throw new ToolError($"index must be 0..{count} within {area.Name}.");
            var before = new HashSet<string>(ras.SectionNames(), StringComparer.OrdinalIgnoreCase);
            var section = new RD.Section { Kind = area.Kind, Height = (int?)a["height"] ?? 300 };
            ras.ReportDef.ReportSectionController.Add(section, area, index);
            var name = ras.SectionNames().FirstOrDefault(n => !before.Contains(n));
            return new JObject
            {
                ["section"] = name, ["area"] = area.Name, ["kind"] = area.Kind.ToString().Replace("crAreaSectionKind", ""),
                ["height"] = ras.Section(name).Height, ["index"] = index, ["action"] = "added"
            };
        });

        private static JToken DeleteSection(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);
            var area = ras.Areas().First(x => x.Sections.Cast<RD.Section>().Any(s => Eq(s.Name, section.Name)));
            if (area.Sections.Count == 1)
                throw new ToolError($"'{section.Name}' is the only section of {area.Name}; Crystal cannot remove it. " +
                                    (area.Kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupHeader || area.Kind == RD.CrAreaSectionKindEnum.crAreaSectionKindGroupFooter
                                        ? "Use delete_group to remove the group, or set_section_props suppress=true to hide it."
                                        : "Hide it with set_section_props suppress=true instead."));

            // Objects inside go with the section; lines/boxes from other sections that end in it would dangle.
            var inside = ras.Objects().Where(o => Eq(o.SectionName, section.Name)).Select(o => o.Name).ToList();
            var ending = ReportIO.Scope(main, Sub(a)).ReportDefinition.ReportObjects.OfType<E.DrawingObject>()
                .Where(d => Eq(d.EndSectionName, section.Name) && !inside.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).Select(d => d.Name).ToList();
            var doomed = inside.Concat(ending).ToList();
            if (doomed.Count > 0 && !((bool?)a["force"] ?? false))
                throw new ToolError($"Section '{section.Name}' still has objects: {string.Join(", ", inside)}" +
                                    (ending.Count > 0 ? $"; lines/boxes ending in it: {string.Join(", ", ending)}" : "") +
                                    ". Move them (move_object) or pass force=true to delete them with the section.");
            foreach (var n in doomed) ras.ReportDef.ReportObjectController.Remove(ras.Object(n));
            ras.ReportDef.ReportSectionController.Remove(section);
            return new JObject { ["section"] = section.Name, ["area"] = area.Name, ["action"] = "deleted", ["removed_objects"] = new JArray(doomed) };
        });

        // ---------- move ----------

        private static JToken MoveObject(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var obj = ras.Object((string)a["object"]);
            var target = ras.Section((string)a["section"]);
            var from = obj.SectionName;
            if (Eq(from, target.Name)) throw new ToolError($"'{obj.Name}' is already in {target.Name}; use set_object_props to change its position.");

            // RAS cannot re-parent an object (Modify ignores SectionName), so re-add a copy and drop the original.
            // The copy keeps name, size, font, formatting and conditional formulas.
            var copy = obj.Clone(true);
            copy.SectionName = target.Name;
            int dx = a["left"] != null ? (int)a["left"] - copy.Left : 0, dy = a["top"] != null ? (int)a["top"] - copy.Top : 0;
            if (copy is RD.ISCRDrawingObject d)
            {
                if (!Eq(d.EndSectionName, from))
                    throw new ToolError($"'{obj.Name}' runs from {from} into {d.EndSectionName}; delete and redraw it with add_line/add_box instead.");
                d.EndSectionName = target.Name;
                // Lines and boxes are defined by both corners.
                d.Right += dx;
                d.Bottom += dy;
            }
            copy.Left += dx;
            copy.Top += dy;
            var oc = ras.ReportDef.ReportObjectController;
            oc.Remove(obj);
            oc.Add(copy, target, -1);
            return new JObject { ["object"] = copy.Name, ["from_section"] = from, ["to_section"] = target.Name, ["box"] = $"{copy.Left},{copy.Top} {copy.Width}x{copy.Height}" };
        });

        private static string TypeText(DD.CrFieldValueTypeEnum t) => t.ToString().Replace("crFieldValueType", "").Replace("Field", "").ToLowerInvariant();
    }
}
