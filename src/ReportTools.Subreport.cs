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

            foreach (var item in requested)
            {
                if (!(item is JObject o) || string.IsNullOrWhiteSpace((string)o["main"]))
                    throw new ToolError("Each link is {\"main\": \"{?Param} or {Table.Field}\", \"sub\": \"{?Param} or {Table.Field}\"}.");
                var mainField = mainRas.Field((string)o["main"]);
                var mainForm = mainField.FormulaForm;
                // Default: the subreport field or parameter with the same name.
                var subField = subRas.Field(string.IsNullOrWhiteSpace((string)o["sub"]) ? mainForm : (string)o["sub"]);
                bool subIsParam = subField.Kind == DD.CrFieldKindEnum.crFieldKindParameterField;
                if (subIsParam && subField.Type != mainField.Type)
                    throw new ToolError($"{mainForm} is {TypeText(mainField.Type)} but the subreport's {subField.FormulaForm} is {TypeText(subField.Type)}.");

                // Crystal's own naming for the parameter behind a field link.
                var param = subIsParam ? subField.FormulaForm : "{?Pm-" + Norm(mainForm, null) + "}";
                if (!subIsParam) filters.Add((subField.FormulaForm, param));
                current.RemoveAll(l => Eq(l.Param, param));
                current.Add((mainForm, subField.FormulaForm, param));
            }

            var links = new RD.SubreportLinks();
            foreach (var l in current)
                links.Add(new RD.SubreportLink { MainReportFieldName = l.Main, SubreportFieldName = l.Sub, LinkedParameterName = l.Param });
            try { controller.SetSubreportLinks(name, links); }
            catch (COMException ex) { throw new ToolError($"Crystal refused the links: {ex.Message.Trim()}"); }

            // Filter the subreport by each new field link; drop the filters of field links that were removed.
            var dd = subDoc.DataDefinition;
            var selection = dd.RecordSelectionFormula ?? "";
            foreach (var r in removed.Where(r => !current.Any(c => Eq(c.Param, r.Param)) && !Eq(r.Sub, r.Param)))
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

        private static string WithoutClause(string selection, string clause)
        {
            if (Eq(selection.Trim(), clause)) return "";
            var suffix = ") and " + clause;
            var s = selection.Trim();
            return s.StartsWith("(") && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s.Substring(1, s.Length - 1 - suffix.Length) : selection;
        }

        internal static JArray LinkList(E.ReportDocument main, string name) =>
            new JArray(main.ReportClientDocument.SubreportController.GetSubreportLinks(name).Cast<RD.SubreportLink>()
                .Select(l => Eq(l.SubreportFieldName, l.LinkedParameterName)
                    ? $"{l.MainReportFieldName} -> {l.SubreportFieldName}"
                    : $"{l.MainReportFieldName} -> {l.SubreportFieldName} (via {l.LinkedParameterName})"));
    }
}
