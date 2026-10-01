using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using CrystalDecisions.Shared;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using DD = CrystalDecisions.ReportAppServer.DataDefModel;

namespace RptMcp
{
    internal static partial class ReportTools
    {
        private static JToken ExportReport(JObject a)
        {
            var output = Path.GetFullPath(Environment.ExpandEnvironmentVariables((string)a["output_path"] ?? throw new ToolError("output_path is required")));
            var image = (string)a["format"] is string f && (Eq(f, "png") || Eq(f, "jpg")) ? f.ToLowerInvariant() : null;
            var format = image != null ? ExportFormatType.PortableDocFormat : ExportFormat((string)a["format"]);
            var dpi = (int?)a["dpi"] ?? 150;
            if (image != null && (dpi < 36 || dpi > 600)) throw new ToolError("dpi must be between 36 and 600.");

            return ReportIO.With((string)a["path"], rd =>
            {
                var logon = ReportIO.GetLogon(a);
                if (logon.HasCredentials || !string.IsNullOrEmpty(logon.Server)) ReportIO.ApplyLogon(rd, logon);

                if (a["data"] is JObject data)
                    foreach (var t in data.Properties())
                        PushData(rd, t.Name, t.Value);

                if (a["parameters"] is JObject ps)
                    foreach (var p in ps.Properties())
                        SetParameter(rd, p.Name, p.Value);

                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if (image == null)
                {
                    ExportToDisk(rd, format, output);
                    return new JObject { ["exported"] = output, ["format"] = format.ToString(), ["size_kb"] = Math.Round(new FileInfo(output).Length / 1024.0, 1) };
                }

                // The runtime cannot export images: export a temporary PDF and render its pages.
                var pdf = Path.Combine(Path.GetTempPath(), "rptmcp-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    ExportToDisk(rd, format, pdf);
                    return RasterizePdf(pdf, output, image == "jpg", dpi, (string)a["pages"]);
                }
                finally
                {
                    try { File.Delete(pdf); } catch { /* temp file */ }
                }
            });
        }

        private static void ExportToDisk(E.ReportDocument rd, ExportFormatType format, string output)
        {
            try
            {
                rd.ExportToDisk(format, output);
            }
            catch (Exception ex) when (ex.Message.IndexOf("parameter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var needed = rd.DataDefinition.ParameterFields.Cast<E.ParameterFieldDefinition>()
                    .Where(p => !p.IsLinked())
                    .Select(p => (string.IsNullOrEmpty(p.ReportName) ? "" : p.ReportName + "::") + $"{p.Name} ({p.ParameterValueKind})");
                throw new ToolError($"{ex.Message}\nParameters: {string.Join(", ", needed)}");
            }
        }

        /// <summary>
        /// Feeds rows to a table instead of querying its database (needed for reports designed against an
        /// ADO.NET DataSet / XML, and handy for previews). Key: table alias, or "Subreport::alias".
        /// Value: an array of row objects, or the path to a .json file holding one.
        /// Columns are typed from the report's own field definitions; missing columns stay null.
        /// </summary>
        private static void PushData(E.ReportDocument main, string key, JToken value)
        {
            string sub = null, alias = key;
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            if (sep > 0) { sub = key.Substring(0, sep); alias = key.Substring(sep + 2); }

            if (value.Type == JTokenType.String)
            {
                var file = Path.GetFullPath(Environment.ExpandEnvironmentVariables((string)value));
                if (!File.Exists(file)) throw new ToolError("Data file not found: " + file);
                value = JToken.Parse(File.ReadAllText(file));
            }
            if (!(value is JArray rows)) throw new ToolError($"data.{key} must be an array of row objects or a .json file path.");

            var rasTable = Ras.For(main, sub).Db.Database.Tables.Cast<DD.Table>().FirstOrDefault(t => Eq(t.Alias, alias))
                           ?? throw new ToolError($"Table '{alias}' not found{(sub == null ? "" : " in " + sub)}.");
            var dt = new DataTable(rasTable.Name);
            foreach (DD.ISCRField f in rasTable.DataFields)
                dt.Columns.Add(f.Name, ClrType(f.Type));

            // Columns the report does not define (the runtime DataSet can carry more than the design schema,
            // and formulas may reference them) are added with a type inferred from the JSON value.
            foreach (var p in rows.OfType<JObject>().SelectMany(r => r.Properties()))
                if (!dt.Columns.Cast<DataColumn>().Any(c => Eq(c.ColumnName, p.Name)))
                    dt.Columns.Add(p.Name, p.Value.Type == JTokenType.Integer ? typeof(long)
                        : p.Value.Type == JTokenType.Float ? typeof(decimal)
                        : p.Value.Type == JTokenType.Boolean ? typeof(bool) : typeof(string));

            foreach (var row in rows.OfType<JObject>())
            {
                var dr = dt.NewRow();
                foreach (var p in row.Properties())
                {
                    var col = dt.Columns.Cast<DataColumn>().First(c => Eq(c.ColumnName, p.Name));
                    dr[col] = p.Value.Type == JTokenType.Null ? DBNull.Value : ToClr(p.Value, col.DataType);
                }
                dt.Rows.Add(dr);
            }

            var doc = ReportIO.Scope(main, sub);
            var table = doc.Database.Tables.Cast<E.Table>().First(t => Eq(t.Name, alias));
            table.SetDataSource(dt);
        }

        private static Type ClrType(DD.CrFieldValueTypeEnum type)
        {
            var n = type.ToString();
            if (n.Contains("Int")) return typeof(long);
            if (n.Contains("Number") || n.Contains("Currency")) return typeof(decimal);
            if (n.Contains("Date") || n.Contains("Time")) return typeof(DateTime);
            if (n.Contains("Boolean")) return typeof(bool);
            if (n.Contains("Blob") || n.Contains("Bitmap") || n.Contains("Picture") || n.Contains("Icon")) return typeof(byte[]);
            return typeof(string);
        }

        private static object ToClr(JToken v, Type type)
        {
            if (type == typeof(byte[]))
            {
                // A blob is given as an image file path or base64.
                var s = (string)v;
                var file = Environment.ExpandEnvironmentVariables(s);
                return File.Exists(file) ? File.ReadAllBytes(file) : System.Convert.FromBase64String(s);
            }
            if (type == typeof(DateTime) && v.Type == JTokenType.String)
                return DateTime.Parse((string)v, CultureInfo.InvariantCulture);
            return v.ToObject(type);
        }

        private static ExportFormatType ExportFormat(string f)
        {
            switch ((f ?? "pdf").ToLowerInvariant())
            {
                case "pdf": return ExportFormatType.PortableDocFormat;
                case "xlsx": return ExportFormatType.ExcelWorkbook;
                case "xls": return ExportFormatType.Excel;
                case "xls_data": return ExportFormatType.ExcelRecord;
                case "doc": return ExportFormatType.WordForWindows;
                case "rtf": return ExportFormatType.RichText;
                case "csv": return ExportFormatType.CharacterSeparatedValues;
                case "txt": return ExportFormatType.Text;
                case "rpt": return ExportFormatType.CrystalReport;
                default: throw new ToolError("Unknown format: " + f);
            }
        }

        private static void SetParameter(E.ReportDocument rd, string key, JToken value)
        {
            string sub = null, name = key;
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            if (sep > 0) { sub = key.Substring(0, sep); name = key.Substring(sep + 2); }
            name = Norm(name, "?");

            var def = rd.DataDefinition.ParameterFields.Cast<E.ParameterFieldDefinition>()
                .FirstOrDefault(p => Eq(p.Name, name) && (sub == null ? string.IsNullOrEmpty(p.ReportName) : Eq(p.ReportName, sub)))
                ?? throw new ToolError($"Parameter '{key}' not found.");

            object Convert(JToken v)
            {
                var s = v.Type == JTokenType.Date ? ((DateTime)v).ToString("s") : v.ToString();
                switch (def.ParameterValueKind)
                {
                    case ParameterValueKind.NumberParameter:
                    case ParameterValueKind.CurrencyParameter: return decimal.Parse(s, CultureInfo.InvariantCulture);
                    case ParameterValueKind.BooleanParameter: return bool.Parse(s);
                    case ParameterValueKind.DateParameter:
                    case ParameterValueKind.DateTimeParameter:
                    case ParameterValueKind.TimeParameter: return DateTime.Parse(s, CultureInfo.InvariantCulture);
                    default: return s;
                }
            }

            object val = value is JArray arr ? arr.Select(Convert).ToArray() : Convert(value);
            if (sub == null) rd.SetParameterValue(def.Name, val);
            else rd.SetParameterValue(def.Name, val, sub);
        }
    }
}
