using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CrystalDecisions.Shared;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>Page setup and Verify Database.</summary>
    internal static partial class ReportTools
    {
        /// <summary>Portrait paper sizes in twips, and the engine enum that names them.</summary>
        private static readonly Dictionary<string, (int W, int H, PaperSize Engine)> Papers = new Dictionary<string, (int, int, PaperSize)>(StringComparer.OrdinalIgnoreCase)
        {
            ["A3"] = (16838, 23811, PaperSize.PaperA3),
            ["A4"] = (11906, 16838, PaperSize.PaperA4),
            ["A5"] = (8391, 11906, PaperSize.PaperA5),
            ["B4"] = (14173, 20013, PaperSize.PaperB4),
            ["B5"] = (10319, 14571, PaperSize.PaperB5),
            ["Letter"] = (12240, 15840, PaperSize.PaperLetter),
            ["Legal"] = (12240, 20160, PaperSize.PaperLegal),
            ["Tabloid"] = (15840, 24480, PaperSize.PaperTabloid),
            ["Executive"] = (10440, 15120, PaperSize.PaperExecutive),
            ["Folio"] = (12240, 18720, PaperSize.PaperFolio),
        };

        /// <summary>
        /// Paper size and orientation go through RAS (a user paper size in twips): the engine's PrintOptions.PaperSize /
        /// PaperOrientation are saved in the file but ignored when the report is rendered without a printer.
        /// </summary>
        private static JToken SetPageSetup(JObject a) => Edit(a, main =>
        {
            if (Sub(a) != null) throw new ToolError("Page setup belongs to the main report; omit subreport.");
            var poc = main.ReportClientDocument.PrintOutputController;
            var current = poc.GetPrintOptions();
            var m = current.PageMargins;
            int curW = current.PageContentWidth + m.Left + m.Right, curH = current.PageContentHeight + m.Top + m.Bottom;
            bool curLandscape = curW > curH;
            var done = new JArray();

            var size = (string)a["size"];
            var orientation = ((string)a["orientation"])?.Trim().ToLowerInvariant();
            if (orientation != null && orientation != "portrait" && orientation != "landscape")
                throw new ToolError("orientation must be portrait or landscape.");
            bool custom = a["width"] != null || a["height"] != null;
            if (custom && size != null) throw new ToolError("Give either size or width/height, not both.");
            if (custom && (a["width"] == null || a["height"] == null)) throw new ToolError("A custom paper size needs both width and height (twips).");

            int w = curW, h = curH;
            PaperSize? engineSize = null;
            if (size != null)
            {
                var key = size.Trim();
                if (key.StartsWith("Paper", StringComparison.OrdinalIgnoreCase)) key = key.Substring(5);
                if (!Papers.TryGetValue(key, out var p)) throw new ToolError($"Unknown size '{size}'. Valid: {string.Join(", ", Papers.Keys)} (or width/height in twips).");
                w = p.W; h = p.H; engineSize = p.Engine;
                bool landscape = orientation != null ? orientation == "landscape" : curLandscape;
                if (landscape) (w, h) = (h, w);
                done.Add("size");
            }
            else if (custom)
            {
                w = (int)a["width"]; h = (int)a["height"];
                if (w < 1440 || h < 1440 || w > 50000 || h > 50000) throw new ToolError("width/height must be 1440..50000 twips (1440 = 1 inch).");
                if (orientation != null && (orientation == "landscape") != (w > h))
                    throw new ToolError("orientation contradicts width/height; for a custom size give the width and height as printed.");
                done.Add("size");
            }
            else if (orientation != null && (orientation == "landscape") != curLandscape)
            {
                (w, h) = (h, w);
            }
            if (orientation != null) done.Add("orientation");

            if (done.Count > 0)
            {
                poc.ModifyUserPaperSize(h, w);
                poc.ModifyPaperOrientation(w > h ? RD.CrPaperOrientationEnum.crPaperOrientationLandscape : RD.CrPaperOrientationEnum.crPaperOrientationPortrait);
            }

            if (a["margin_left"] != null || a["margin_right"] != null || a["margin_top"] != null || a["margin_bottom"] != null)
            {
                int Margin(string k, int old)
                {
                    var v = (int?)a[k] ?? old;
                    if (v < 0 || v > 7200) throw new ToolError($"{k} must be 0..7200 twips.");
                    return v;
                }
                poc.ModifyPageMargins(Margin("margin_left", m.Left), Margin("margin_right", m.Right), Margin("margin_top", m.Top), Margin("margin_bottom", m.Bottom));
                done.Add("margins");
            }
            if (done.Count == 0) throw new ToolError("Nothing to change: pass size, orientation, width/height or a margin.");
            // Keep the engine's paper name in line, so inspect_report shows it (custom sizes have none).
            if (engineSize != null) main.PrintOptions.PaperSize = engineSize.Value;

            var now = poc.GetPrintOptions();
            var nm = now.PageMargins;
            return new JObject
            {
                ["changed"] = done,
                ["size"] = custom ? "custom" : (engineSize ?? main.PrintOptions.PaperSize).ToString().Replace("DefaultPaperSize", "default").Replace("Paper", ""),
                ["orientation"] = w > h ? "landscape" : "portrait",
                ["paper_twips"] = new JObject { ["width"] = w, ["height"] = h },
                ["margins_twips"] = new JObject { ["left"] = nm.Left, ["right"] = nm.Right, ["top"] = nm.Top, ["bottom"] = nm.Bottom },
                ["page_content_width"] = now.PageContentWidth,
                ["page_content_height"] = now.PageContentHeight,
            };
        });

        // ---------- verify database ----------

        /// <summary>
        /// Runs Crystal's Verify Database on a loaded copy and reports what it would change; the file is never saved.
        /// Headless, VerifyDatabase does not complain about a missing column: it silently drops the field and deletes the
        /// objects bound to it, so the result is found by comparing the fields and objects before and after.
        /// </summary>
        private static JToken VerifyDatabase(JObject a) => ReportIO.With((string)a["path"], rd =>
        {
            var scopes = new List<string> { null }.Concat(Ras.SubreportNames(rd)).ToList();
            string Prefix(string scope) => scope == null ? "" : scope + "::";
            Dictionary<string, string> Fields() => scopes.SelectMany(s => Ras.For(rd, s).Db.Database.Tables.Cast<DD.Table>()
                    .SelectMany(t => t.DataFields.Cast<DD.ISCRField>().Select(f => (Key: Prefix(s) + f.FormulaForm, Type: TypeText(f.Type)))))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
            HashSet<string> Objects() => new HashSet<string>(scopes.SelectMany(s => Ras.For(rd, s).Objects().Select(o => Prefix(s) + o.Name)), StringComparer.OrdinalIgnoreCase);

            var tables = new JArray(scopes.SelectMany(s => Ras.For(rd, s).Db.Database.Tables.Cast<DD.Table>().Select(t =>
                $"{Prefix(s)}{t.Alias} -> {(t is DD.CommandTable ? "SQL command" : t.QualifiedName)}")));
            var first = Ras.For(rd, null).Db.Database.Tables.Cast<DD.Table>().FirstOrDefault()?.ConnectionInfo?.Attributes;
            if (tables.Count == 0) throw new ToolError("The report has no tables to verify.");
            var logon = ReportIO.GetLogon(a);
            var result = Compact(new JObject
            {
                ["tables"] = tables,
                ["server"] = logon.Server ?? Prop(first, "QE_ServerDescription"),
                ["database"] = logon.Database ?? Prop(first, "QE_DatabaseName"),
            });
            if (logon.HasCredentials || !string.IsNullOrEmpty(logon.Server)) ReportIO.ApplyLogon(rd, logon);

            var fieldsBefore = Fields();
            var objectsBefore = Objects();

            try
            {
                rd.VerifyDatabase();
            }
            catch (Exception ex) when (ex is COMException || ex is E.EngineException)
            {
                result["verified"] = false;
                result["error"] = ex.Message.Trim();
                result["hint"] = VerifyHint(rd, logon, ex);
                return Compact(result);
            }

            var fieldsAfter = Fields();
            var missing = fieldsBefore.Keys.Where(k => !fieldsAfter.ContainsKey(k)).ToList();
            var added = fieldsAfter.Keys.Where(k => !fieldsBefore.ContainsKey(k)).ToList();
            var changedType = fieldsBefore.Keys.Where(k => fieldsAfter.TryGetValue(k, out var t) && t != fieldsBefore[k])
                .Select(k => $"{k}: {fieldsBefore[k]} -> {fieldsAfter[k]}").ToList();
            var objectsAfter = Objects();
            var lostObjects = objectsBefore.Where(o => !objectsAfter.Contains(o)).ToList();

            result["verified"] = missing.Count == 0 && changedType.Count == 0 && lostObjects.Count == 0;
            if (missing.Count > 0)
            {
                // Verifying already deleted the objects bound to the missing fields, so look them up in a fresh copy.
                var usedBy = ReportIO.With((string)a["path"], fresh => missing.ToDictionary(k => k, k =>
                {
                    var sep = k.IndexOf("::", StringComparison.Ordinal);
                    var scope = sep > 0 ? k.Substring(0, sep) : null;
                    return Usages(fresh, scope, Ras.For(fresh, scope), new[] { sep > 0 ? k.Substring(sep + 2) : k }, out _);
                }));
                result["missing_fields"] = new JArray(missing.Select(k => usedBy[k].Count == 0
                    ? (JToken)k : new JObject { ["field"] = k, ["used_by"] = new JArray(usedBy[k].Distinct()) }));
            }
            if (changedType.Count > 0) result["changed_types"] = new JArray(changedType);
            if (added.Count > 0) result["new_columns"] = new JArray(added);
            if (lostObjects.Count > 0) result["objects_that_would_be_deleted"] = new JArray(lostObjects);
            if (missing.Count > 0)
                result["hint"] = "The database no longer has these columns (renamed or dropped). Fix the database, or change the report " +
                                 "(formulas/objects using them) before saving; Crystal's own Verify Database would delete the bound objects.";
            return result;
        });

        private static string VerifyHint(E.ReportDocument rd, ReportIO.Logon logon, Exception ex)
        {
            var t = Ras.For(rd, null).Db.Database.Tables.Cast<DD.Table>().FirstOrDefault();
            var lp = Prop2(t?.ConnectionInfo?.Attributes, "QE_LogonProperties") as DD.PropertyBag;
            var provider = Prop(lp, "Provider");
            var server = logon.Server ?? Prop(t?.ConnectionInfo?.Attributes, "QE_ServerDescription") ?? "";
            if (Eq(provider, "SQLOLEDB") && server.IndexOf("localdb", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The legacy SQLOLEDB provider cannot reach LocalDB; run set_datasource with provider MSOLEDBSQL.";
            if (Eq(provider, "SQLOLEDB"))
                return "If the server needs TLS 1.2, the legacy SQLOLEDB provider cannot connect; run set_datasource with provider MSOLEDBSQL.";
            if (ex is E.LogOnException)
                return "Check server, database and credentials (user/password or integrated), e.g. with a named connection; set_datasource changes the saved ones.";
            return null;
        }
    }
}
