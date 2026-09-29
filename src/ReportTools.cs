using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using static RptMcp.ToolRegistry;

namespace RptMcp
{
    /// <summary>Tool definitions. Implementations live in ReportTools.*.cs.</summary>
    internal static partial class ReportTools
    {
        private static readonly JProperty PathArg = Req("path", "string", "Absolute path to the .rpt file.");
        private static readonly JProperty SubArg = Opt("subreport", "string", "Subreport name to operate on (omit for the main report).");
        private static readonly JProperty OutArg = Opt("output_path", "string", "Save to this .rpt instead of overwriting 'path' (when overwriting, a backup is made in _rptmcp_backup).");

        private static JProperty[] LogonArgs(string purpose) => new[]
        {
            Opt("connection", "string", $"Named connection from connections.json ({purpose})."),
            Opt("server", "string", "DB server (overrides connection)."),
            Opt("database", "string", "DB name (overrides connection)."),
            Opt("user", "string", "DB user (overrides connection)."),
            Opt("password", "string", "DB password (overrides connection)."),
            Opt("integrated", "boolean", "Use Windows integrated security."),
        };

        private static ToolRegistry _registry;

        public static void Register(ToolRegistry r)
        {
            _registry = r;

            r.Add("batch_edit",
                "Apply several edits in one go: the report is loaded once, backed up once and saved once. All-or-nothing: " +
                "if any operation fails, nothing is saved. Prefer this over separate calls when making more than one change. " +
                "Each operation is {\"tool\": \"<edit tool>\", ...that tool's arguments without path/output_path}, e.g. " +
                "[{\"tool\":\"set_text\",\"object\":\"Text3\",\"text\":\"Hi\"},{\"tool\":\"set_object_props\",\"object\":\"Text3\",\"bold\":true}]. " +
                "Allowed tools: " + string.Join(", ", EditTools) + ".",
                Schema(PathArg, Req("operations", "object[]", "Edit operations, applied in order."), OutArg),
                BatchEdit);

            // ---------- read ----------
            r.Add("list_reports", "List .rpt files in a folder (backups in _rptmcp_backup are skipped).",
                Schema(Req("folder", "string", "Folder to scan."),
                       Opt("recursive", "boolean", "Include subfolders (default true)."),
                       Opt("filter", "string", "Case-insensitive substring the file name must contain.")),
                ListReports);

            r.Add("inspect_report",
                "Describe a report: page setup, tables/SQL commands/connections, links, parameters, formulas, selection formulas, groups, sorts, " +
                "sections and every report object (name, kind, position/size in twips, text or bound field, font, suppress), plus the conditional formulas " +
                "hidden in object/section formatting (e.g. Format.EnableSuppress, FontColor.Color). " +
                "Call this before editing to learn exact names.",
                Schema(PathArg, SubArg,
                       Opt("include_objects", "boolean", "Include report objects per section (default true)."),
                       Opt("include_fields", "boolean", "Include the column list of every table (default false)."),
                       Opt("include_subreports", "boolean", "Also inspect every subreport, nested under 'subreport_details' (default false).")),
                InspectReport);

            r.Add("diff_reports", "Structural diff between two .rpt files (e.g. a backup vs. the edited file), including subreports.",
                Schema(Req("path_a", "string", "Original .rpt."), Req("path_b", "string", "Changed .rpt.")),
                DiffReports);

            // ---------- data definition ----------
            r.Add("set_formula", "Set the text of a formula field; creates it when it does not exist.",
                Schema(PathArg, Req("name", "string", "Formula name, with or without {@ }."), Req("text", "string", "Formula text."),
                       Enum("syntax", "Syntax for a NEW formula (default crystal).", false, "crystal", "basic"),
                       SubArg, OutArg),
                SetFormula);

            r.Add("delete_formula", "Delete a formula field. Refused while it is used anywhere (field objects, fields embedded in text objects, formulas, selection formulas, conditional formulas, groups, sorts, SQL commands), unless force=true.",
                Schema(PathArg, Req("name", "string", "Formula name."),
                       Opt("force", "boolean", "Delete anyway; objects bound to it are deleted too."), SubArg, OutArg),
                DeleteFormula);

            r.Add("set_selection_formula", "Set the record or group selection formula (empty string clears it).",
                Schema(PathArg, Req("formula", "string", "Selection formula text."),
                       Enum("kind", "Which selection formula (default record).", false, "record", "group"),
                       SubArg, OutArg),
                SetSelectionFormula);

            r.Add("add_parameter", "Add a report parameter.",
                Schema(new[]
                {
                    PathArg, Req("name", "string", "Parameter name (without {? })."),
                    Enum("type", "Value type (default string).", false, "string", "number", "currency", "boolean", "date", "datetime", "time"),
                    Opt("prompt", "string", "Prompt text."),
                    Opt("allow_multiple", "boolean", "Allow multiple values."),
                    Opt("default_values", "string[]", "Default values offered in the prompt."),
                    SubArg, OutArg
                }),
                AddParameter);

            r.Add("delete_parameter", "Delete a report parameter. Refused while it is used anywhere (field objects, fields embedded in text objects, formulas, selection formulas, conditional formulas, groups, sorts, SQL commands), unless force=true.",
                Schema(PathArg, Req("name", "string", "Parameter name."),
                       Opt("force", "boolean", "Delete anyway; objects bound to it are deleted too."), SubArg, OutArg),
                DeleteParameter);

            // ---------- database ----------
            r.Add("set_command_sql",
                "Replace the SQL text of a SQL Command table. Crystal validates the SQL against the database, so a working connection is usually required.",
                Schema(new[] { PathArg, Req("sql", "string", "New SQL text."),
                               Opt("table", "string", "Command alias (default: the first command table)."), SubArg, OutArg }
                       .Concat(LogonArgs("used to validate the SQL")).ToArray()),
                SetCommandSql);

            r.Add("remove_table",
                "Remove a table or SQL command from the report. Refused while any of its fields is used, unless force=true. " +
                "Even with force, formulas / selection formulas that reference it must be changed first (Crystal refuses otherwise).",
                Schema(PathArg, Req("table", "string", "Table alias."),
                       Opt("force", "boolean", "Delete the objects bound to its fields, then remove the table."), SubArg, OutArg),
                RemoveTable);

            r.Add("set_datasource",
                "Point tables at another server/database and persist it in the .rpt (e.g. switch a template from a dev to a production database). " +
                "Applies to the main report and all subreports unless 'subreport' is given.",
                Schema(new[] { PathArg, Opt("table", "string", "Only this table alias (default all)."),
                               Opt("provider", "string", "OLE DB provider to switch to, e.g. MSOLEDBSQL (the legacy SQLOLEDB cannot reach LocalDB or TLS 1.2-only servers)."),
                               SubArg, OutArg }
                       .Concat(LogonArgs("target server/database + credentials")).ToArray()),
                SetDatasource);

            // ---------- layout ----------
            r.Add("set_text", "Replace the text of a text object (or field heading).",
                Schema(PathArg, Req("object", "string", "Object name, e.g. Text12."), Req("text", "string", "New text."), SubArg, OutArg),
                SetText);

            r.Add("set_object_props", "Change position/size (twips), font, color, alignment, suppress or can-grow of a report object.",
                Schema(PathArg, Req("object", "string", "Object name."),
                       Opt("left", "integer", "Left (twips)."), Opt("top", "integer", "Top (twips)."),
                       Opt("width", "integer", "Width (twips)."), Opt("height", "integer", "Height (twips)."),
                       Opt("font_name", "string", "Font family."), Opt("font_size", "number", "Font size in points."),
                       Opt("bold", "boolean", "Bold."), Opt("italic", "boolean", "Italic."), Opt("underline", "boolean", "Underline."),
                       Opt("color", "string", "Text color, #RRGGBB or a color name."),
                       Enum("align", "Horizontal alignment.", false, "default", "left", "center", "right", "justified"),
                       Opt("suppress", "boolean", "Suppress (hide) the object."),
                       Opt("can_grow", "boolean", "Can grow."),
                       Opt("right", "integer", "Line/box: right edge (twips)."), Opt("bottom", "integer", "Line/box: bottom edge (twips)."),
                       Enum("line_style", "Line/box border style.", false, "none", "single", "dashed", "dotted"),
                       Opt("line_thickness", "integer", "Line/box thickness in twips (20 = 1pt)."),
                       Opt("line_color", "string", "Line/box color, #RRGGBB."),
                       Opt("fill_color", "string", "Box fill color #RRGGBB, or \"\" for none."),
                       Opt("extend_to_bottom", "boolean", "Line/box: extend to the bottom of the section."),
                       SubArg, OutArg),
                SetObjectProps);

            r.Add("set_section_props", "Change height (twips), suppress, page break or keep-together of a section.",
                Schema(PathArg, Req("section", "string", "Section name, e.g. DetailSection1 or PageHeaderSection1."),
                       Opt("height", "integer", "Height (twips)."), Opt("suppress", "boolean", "Suppress."),
                       Opt("new_page_before", "boolean", "New page before."), Opt("new_page_after", "boolean", "New page after."),
                       Opt("keep_together", "boolean", "Keep together."), Opt("background_color", "string", "#RRGGBB, or empty for none."),
                       SubArg, OutArg),
                SetSectionProps);

            r.Add("add_text_object", "Add a text object to a section. Use set_object_props afterwards for font/color/alignment.",
                Schema(PathArg, Req("section", "string", "Section name."), Req("text", "string", "Text."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Req("width", "integer", "Width (twips)."), Req("height", "integer", "Height (twips)."),
                       Opt("name", "string", "Object name (default: assigned by Crystal)."), SubArg, OutArg),
                AddTextObject);

            r.Add("add_field_object",
                "Add a field object bound to a database field, formula, parameter, SQL expression or running total, e.g. {Orders.OrderNo}, {@Total}, {?StartDate}.",
                Schema(PathArg, Req("section", "string", "Section name."), Req("field", "string", "Field in formula form, e.g. {Table.Column}."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Req("width", "integer", "Width (twips)."), Req("height", "integer", "Height (twips)."),
                       Opt("name", "string", "Object name (default: assigned by Crystal)."), SubArg, OutArg),
                AddFieldObject);

            r.Add("set_field_format",
                "Format how a field object displays numbers, dates, times or booleans. Pass only the options to change. " +
                "For anything the options cannot express, use display_format with a pattern (numbers: \"#,##0.00\", \"0.0%\"; " +
                "dates: \"yyyy-MM-dd\", \"dd/MM/yyyy HH:mm\"), applied through the Display String conditional formula; \"\" removes it.",
                Schema(PathArg, Req("object", "string", "Field object name, or a text object that embeds fields."),
                       Opt("field", "string", "For a text object embedding several fields: which one, e.g. {Orders.Qty}."),
                       Opt("decimals", "integer", "Decimal places 0..10 (rounding follows unless 'rounding' is given)."),
                       Opt("rounding", "integer", "Round to this many decimals 0..10."),
                       Opt("thousands_separator", "boolean", "Show a thousands separator."),
                       Opt("thousands_symbol", "string", "Thousands separator character, e.g. \".\"."),
                       Opt("decimal_symbol", "string", "Decimal symbol, e.g. \",\"."),
                       Enum("negative", "Negative number style.", false, "none", "leading", "trailing", "brackets"),
                       Opt("leading_zero", "boolean", "Show a leading zero (0.5 instead of .5)."),
                       Opt("currency_symbol", "string", "Currency symbol, e.g. \"Rp \" (\"\" removes it)."),
                       Enum("currency", "Currency symbol placement.", false, "none", "fixed", "floating"),
                       Opt("suppress_if_zero", "boolean", "Hide the value when it is zero."),
                       Opt("zero_value_string", "string", "Text to show instead of zero, e.g. \"-\"."),
                       Enum("date_order", "Date order.", false, "ymd", "dmy", "mdy"),
                       Opt("date_separator", "string", "Separator between date parts, e.g. \"-\" or \"/\"."),
                       Enum("year", "Year style.", false, "short", "long", "none"),
                       Enum("month", "Month style.", false, "numeric", "leading_zero", "short", "long", "none"),
                       Enum("day", "Day style.", false, "numeric", "leading_zero", "none"),
                       Enum("time_base", "12 or 24 hour clock.", false, "12", "24"),
                       Opt("show_seconds", "boolean", "Show seconds."),
                       Opt("time_separator", "string", "Separator between hours, minutes and seconds."),
                       Opt("am_string", "string", "AM text (12-hour clock)."), Opt("pm_string", "string", "PM text (12-hour clock)."),
                       Enum("datetime_order", "What a date-time field shows.", false, "date_time", "time_date", "date", "time"),
                       Opt("datetime_separator", "string", "Text between the date and the time."),
                       Enum("boolean_output", "How booleans print.", false, "true_false", "t_f", "yes_no", "y_n", "1_0"),
                       Opt("suppress_if_duplicated", "boolean", "Hide the value when it equals the previous record's."),
                       Opt("display_format", "string", "Custom ToText pattern (overrides the options above when printing)."),
                       SubArg, OutArg),
                SetFieldFormat);

            r.Add("set_condition_formula",
                "Set or clear (empty formula) a conditional formula on a report object, e.g. suppress when a value is empty. " +
                "condition uses the same paths inspect_report shows: Format.EnableSuppress, Format.DisplayString, Format.HorizontalAlignment, " +
                "Format.ToolTipText, Format.Hyperlink, FontColor.Color, FontColor.Style, FontColor.Size, Border.BackgroundColor, Border.BorderColor, " +
                "FieldFormat.NumericFormat.NDecimalPlaces, FieldFormat.CommonFormat.SuppressIfDuplicated, …",
                Schema(PathArg, Req("object", "string", "Object name."),
                       Req("condition", "string", "Condition path, e.g. Format.EnableSuppress."),
                       Req("formula", "string", "Crystal formula, e.g. IsNull({T.Col}) or {T.Col} = \"\". Use crRed etc. for colors. \"\" clears it."),
                       SubArg, OutArg),
                SetConditionFormula);

            r.Add("add_line", "Add a horizontal or vertical line. A vertical line may run into a later section (end_section), e.g. table column borders.",
                Schema(PathArg, Req("section", "string", "Section where the line starts."),
                       Req("x1", "integer", "Start x (twips)."), Req("y1", "integer", "Start y (twips)."),
                       Req("x2", "integer", "End x (twips)."), Req("y2", "integer", "End y (twips; in end_section when given)."),
                       Opt("end_section", "string", "Section where the line ends (default: same section)."),
                       Enum("line_style", "Style (default single). Crystal draws dashed and dotted lines as hairlines.", false, "none", "single", "dashed", "dotted"),
                       Opt("line_thickness", "integer", "Thickness in twips (default 20 = 1pt)."),
                       Opt("line_color", "string", "Color #RRGGBB (default black)."),
                       Opt("name", "string", "Object name."), SubArg, OutArg),
                AddLine);

            r.Add("add_box", "Add a box (rectangle), optionally filled and with rounded corners.",
                Schema(PathArg, Req("section", "string", "Section where the box starts."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Req("right", "integer", "Right (twips)."), Req("bottom", "integer", "Bottom (twips; in end_section when given)."),
                       Opt("end_section", "string", "Section where the box ends (default: same section)."),
                       Enum("line_style", "Border style (default single).", false, "none", "single", "dashed", "dotted"),
                       Opt("line_thickness", "integer", "Border thickness in twips (default 20)."),
                       Opt("line_color", "string", "Border color #RRGGBB (default black)."),
                       Opt("fill_color", "string", "Fill color #RRGGBB (default none)."),
                       Opt("corner_radius", "integer", "Rounded corner radius in twips."),
                       Opt("name", "string", "Object name."), SubArg, OutArg),
                AddBox);

            r.Add("add_picture", "Insert an image file (png, jpg, bmp, gif, tif) into a section. Give width or height to scale proportionally, or both.",
                Schema(PathArg, Req("section", "string", "Section name."), Req("image_path", "string", "Image file."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Opt("width", "integer", "Width (twips)."), Opt("height", "integer", "Height (twips)."),
                       Opt("name", "string", "Object name."), SubArg, OutArg),
                AddPicture);

            r.Add("replace_picture", "Replace the image of a picture object (e.g. a logo), keeping its name, position and (by default) size.",
                Schema(PathArg, Req("object", "string", "Picture object name, e.g. Picture1."), Req("image_path", "string", "New image file."),
                       Opt("keep_size", "boolean", "Keep the old width/height (default true); false uses the image's own size."),
                       SubArg, OutArg),
                ReplacePicture);

            r.Add("delete_object", "Delete a report object (text, field, line, box, picture, subreport...).",
                Schema(PathArg, Req("object", "string", "Object name."), SubArg, OutArg),
                DeleteObject);

            // ---------- run ----------
            r.Add("export_report",
                "Run the report against the database and export it (PDF is best for visually checking a change). " +
                "Parameter values: {\"Name\": value} or {\"Name\": [v1, v2]}; for an unlinked subreport parameter use \"Subreport::Name\".",
                Schema(new[]
                {
                    PathArg, Req("output_path", "string", "Output file."),
                    Enum("format", "Export format (default pdf).", false, "pdf", "xlsx", "xls", "xls_data", "doc", "rtf", "csv", "txt", "rpt"),
                    Opt("parameters", "object", "Parameter values by name."),
                    Opt("data", "object",
                        "Rows to use instead of querying the database (for DataSet/XML-based reports or quick previews): " +
                        "{\"<table alias>\": [ {\"col\": value, ...}, ... ]} or {\"<alias>\": \"C:\\\\rows.json\"}. " +
                        "Blob/picture columns take an image file path or base64."),
                }.Concat(LogonArgs("DB logon for running the report")).ToArray()),
                ExportReport);
        }

        // ---------- list ----------

        private static JToken ListReports(JObject a)
        {
            var folder = Path.GetFullPath((string)a["folder"] ?? throw new ToolError("folder is required"));
            if (!Directory.Exists(folder)) throw new ToolError("Folder not found: " + folder);
            var filter = (string)a["filter"];
            var option = ((bool?)a["recursive"] ?? true) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var files = Directory.EnumerateFiles(folder, "*.rpt", option)
                .Where(f => !f.Split(Path.DirectorySeparatorChar).Contains(ReportIO.BackupFolder, StringComparer.OrdinalIgnoreCase))
                .Where(f => string.IsNullOrEmpty(filter) || Path.GetFileName(f).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.FullName)
                .Select(f => new JObject
                {
                    ["path"] = f.FullName,
                    ["size_kb"] = Math.Round(f.Length / 1024.0, 1),
                    ["modified"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                });
            return new JObject { ["folder"] = folder, ["reports"] = new JArray(files) };
        }

        // ---------- shared helpers ----------

        internal static string Norm(string name, string prefix)
        {
            if (name == null) return null;
            name = name.Trim();
            if (name.StartsWith("{") && name.EndsWith("}")) name = name.Substring(1, name.Length - 2);
            if (prefix != null && name.StartsWith(prefix)) name = name.Substring(prefix.Length);
            return name.Trim();
        }

        internal static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
