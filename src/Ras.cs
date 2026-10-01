using System;
using System.Collections.Generic;
using System.Linq;
using CrystalDecisions.ReportAppServer.Controllers;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>
    /// Report Application Server (in-process) controllers for the main report or a subreport.
    /// RAS is the only API that can structurally modify a report (add/remove objects, formulas, tables, SQL).
    /// </summary>
    internal sealed class Ras
    {
        public DatabaseController Db;
        public DataDefController DataDef;
        public ReportDefController2 ReportDef;

        public static Ras For(E.ReportDocument main, string subreport)
        {
            var rcd = main.ReportClientDocument;
            if (string.IsNullOrWhiteSpace(subreport))
                return new Ras { Db = rcd.DatabaseController, DataDef = rcd.DataDefController, ReportDef = rcd.ReportDefController };
            var sub = rcd.SubreportController.GetSubreport(subreport);
            return new Ras { Db = sub.DatabaseController, DataDef = sub.DataDefController, ReportDef = sub.ReportDefController };
        }

        /// <summary>
        /// Calls Add/Modify/Remove on the running total controller. The platform-neutral reference assembly of some
        /// runtime SPs lacks RunningTotalFieldController although the loaded (64-bit) one has it, so it is late-bound.
        /// </summary>
        public object RunningTotals(string method, params object[] args)
        {
            var prop = typeof(ISCRDataDefController).GetProperty("RunningTotalFieldController")
                       ?? throw new ToolError("This Crystal Reports runtime does not support running totals (no RunningTotalFieldController).");
            var controller = prop.GetValue(DataDef);
            // The property type is the coclass interface; the methods live on the interfaces it inherits.
            var m = new[] { prop.PropertyType }.Concat(prop.PropertyType.GetInterfaces()).Select(t => t.GetMethod(method)).FirstOrDefault(x => x != null)
                    ?? throw new ToolError($"The running total controller has no {method} method in this Crystal Reports runtime.");
            try { return m.Invoke(controller, args); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public IEnumerable<RD.Area> Areas() => ReportDef.ReportDefinition.Areas.Cast<RD.Area>();

        public static IEnumerable<string> SubreportNames(E.ReportDocument main) =>
            main.Subreports.Cast<E.ReportDocument>().Select(s => s.Name).ToList();

        public void Logon(ReportIO.Logon l)
        {
            if (l != null && !l.Integrated && !string.IsNullOrEmpty(l.User))
                Db.logon(l.User, l.Password ?? "");
        }

        public RD.Section Section(string name)
        {
            foreach (RD.Area area in ReportDef.ReportDefinition.Areas)
                foreach (RD.Section s in area.Sections)
                    if (ReportTools.Eq(s.Name, name)) return s;
            throw new ToolError($"Section '{name}' not found. Sections: {string.Join(", ", SectionNames())}");
        }

        public IEnumerable<string> SectionNames()
        {
            foreach (RD.Area area in ReportDef.ReportDefinition.Areas)
                foreach (RD.Section s in area.Sections)
                    yield return s.Name;
        }

        /// <summary>Section name / object name → (condition path → formula text), only for items that have any.</summary>
        public Dictionary<string, Dictionary<string, string>> AllConditions()
        {
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (RD.Area area in ReportDef.ReportDefinition.Areas)
                foreach (RD.Section s in area.Sections)
                {
                    var c = Conditions.Scan(s, typeof(RD.Section));
                    if (c.Count > 0) all[s.Name] = c;
                }
            foreach (var o in Objects())
            {
                var c = Conditions.Scan(o, DeclaredType(o));
                if (c.Count > 0) all[o.Name] = c;
            }
            return all;
        }

        /// <summary>The specific RAS interface for an object's kind (text objects carry FontColor, fields carry FieldFormat...).</summary>
        internal static Type DeclaredType(RD.ISCRReportObject o)
        {
            switch (o.Kind)
            {
                case RD.CrReportObjectKindEnum.crReportObjectKindText: return typeof(RD.TextObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindField: return typeof(RD.FieldObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindFieldHeading: return typeof(RD.FieldHeadingObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindLine: return typeof(RD.LineObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindBox: return typeof(RD.BoxObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindPicture: return typeof(RD.PictureObject);
                case RD.CrReportObjectKindEnum.crReportObjectKindSubreport: return typeof(RD.SubreportObject);
                default: return typeof(RD.ReportObject);
            }
        }

        public List<RD.ISCRReportObject> Objects() =>
            ReportDef.ReportObjectController.GetAllReportObjects().Cast<RD.ISCRReportObject>().ToList();

        public RD.ISCRReportObject Object(string name) =>
            Objects().FirstOrDefault(o => ReportTools.Eq(o.Name, name))
            ?? throw new ToolError($"Object '{name}' not found. Run inspect_report to list object names.");

        /// <summary>Finds a field by formula form: {Table.Col}, {@Formula}, {?Param}, {%SqlExpr}, {#RunningTotal}.</summary>
        public DD.ISCRField Field(string formulaForm)
        {
            var wanted = formulaForm.Trim();
            if (!wanted.StartsWith("{")) wanted = "{" + wanted + "}";

            foreach (DD.Table t in Db.Database.Tables)
                foreach (DD.ISCRField f in t.DataFields)
                    if (ReportTools.Eq(f.FormulaForm, wanted)) return f;

            var def = DataDef.DataDefinition;
            foreach (var coll in new DD.Fields[] { def.FormulaFields, def.ParameterFields, def.RunningTotalFields, def.SummaryFields })
            {
                if (coll == null) continue;
                foreach (DD.ISCRField f in coll)
                    if (ReportTools.Eq(f.FormulaForm, wanted)) return f;
            }
            throw new ToolError($"Field {wanted} not found. Use inspect_report with include_fields=true to see available fields.");
        }
    }
}
