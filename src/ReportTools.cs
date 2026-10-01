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

            r.Add("set_parameter",
                "Change an existing parameter in place (prompt, type, multiple values, default values), keeping every formula and object that uses it. " +
                "Default values are the choices offered in the prompt; export_report still needs the parameter values.",
                Schema(new[]
                {
                    PathArg, Req("name", "string", "Parameter name, with or without {? }."),
                    Opt("prompt", "string", "New prompt text."),
                    Enum("type", "New value type (refused while the parameter is used, unless force=true).", false, "string", "number", "currency", "boolean", "date", "datetime", "time"),
                    Opt("allow_multiple", "boolean", "Allow multiple values."),
                    Opt("default_values", "string[]", "New default values ([] removes them)."),
                    Opt("force", "boolean", "Allow changing the type of a parameter that is in use."),
                    SubArg, OutArg
                }),
                SetParameter);

            r.Add("delete_parameter", "Delete a report parameter. Refused while it is used anywhere (field objects, fields embedded in text objects, formulas, selection formulas, conditional formulas, groups, sorts, SQL commands), unless force=true.",
                Schema(PathArg, Req("name", "string", "Parameter name."),
                       Opt("force", "boolean", "Delete anyway; objects bound to it are deleted too."), SubArg, OutArg),
                DeleteParameter);

            r.Add("add_group",
                "Group the report on a field, e.g. per shift or per day. Crystal adds a Group Header and Group Footer section (named after the field, " +
                "e.g. ShiftHeaderSection1 / ShiftFooterSection1); their names are returned so objects can be placed there. " +
                "For a subtotal put a formula like Sum({T.Qty}, {T.Shift}) in the group footer.",
                Schema(PathArg, Req("field", "string", "Field to group on, e.g. {Orders.Shift}."),
                       Opt("index", "integer", "Group position: 0 = outermost (default: after the existing groups, i.e. innermost)."),
                       Enum("date_condition", "Date/time fields only: one group per ... (default daily; second for time fields).", false,
                            "daily", "weekly", "biweekly", "semimonthly", "monthly", "quarterly", "semiannually", "annually", "second", "minute", "hour", "ampm"),
                       Enum("direction", "Group order (default ascending).", false, "ascending", "descending"),
                       SubArg, OutArg),
                AddGroup);

            r.Add("delete_group",
                "Remove a group with its header and footer sections. Refused while those sections hold objects or formulas/running totals summarize per this group, unless force=true.",
                Schema(PathArg, Req("field", "string", "Field the group is on, e.g. {Orders.Shift}."),
                       Opt("force", "boolean", "Delete the objects in its sections too; per-group summaries will break."), SubArg, OutArg),
                DeleteGroup);

            r.Add("add_sort",
                "Sort the records by a field (record sort), e.g. newest first. Without groups this orders the details; with groups it orders the records inside each group. " +
                "On a field that is already sorted (including a group's own field) it changes the direction.",
                Schema(PathArg, Req("field", "string", "Field to sort on, e.g. {Orders.Date}."),
                       Enum("direction", "Direction (default ascending).", false, "ascending", "descending"),
                       Opt("index", "integer", "Position among the record sorts, 0 = first (default: last)."),
                       SubArg, OutArg),
                AddSort);

            r.Add("delete_sort", "Remove a record sort. A group's sort is removed with delete_group.",
                Schema(PathArg, Req("field", "string", "Sorted field, e.g. {Orders.Date}."), SubArg, OutArg),
                DeleteSort);

            r.Add("add_running_total",
                "Create a running total field {#Name}: an accumulating sum/count/... that can be evaluated conditionally and reset per group, field change or formula. " +
                "For a plain subtotal per group a formula like Sum({T.Qty}, {T.Group}) is simpler. Place it with add_field_object field {#Name}.",
                Schema(PathArg, Req("name", "string", "Running total name (without {# })."),
                       Req("field", "string", "Field to summarize, e.g. {Orders.Qty}."),
                       Enum("operation", "Summary operation (default sum).", false, "sum", "count", "distinct_count", "average", "min", "max"),
                       Enum("evaluate", "When a record is counted (default each_record).", false, "each_record", "on_change_of_field", "on_change_of_group", "on_formula"),
                       Opt("evaluate_on", "string", "The field (on_change_of_field), the group's field (on_change_of_group) or the Boolean formula (on_formula) for evaluate."),
                       Enum("reset", "When it starts again from zero (default never).", false, "never", "on_change_of_field", "on_change_of_group", "on_formula"),
                       Opt("reset_on", "string", "The field, the group's field or the Boolean formula for reset, e.g. {Orders.Shift}."),
                       SubArg, OutArg),
                AddRunningTotal);

            r.Add("delete_running_total",
                "Delete a running total (and the formula add_field_object made to show it). Refused while it is used anywhere, unless force=true.",
                Schema(PathArg, Req("name", "string", "Running total name."),
                       Opt("force", "boolean", "Delete anyway; objects showing it are deleted too."), SubArg, OutArg),
                DeleteRunningTotal);

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

            r.Add("add_table",
                "Add a database table to the report (OLE DB / ADO, e.g. SQL Server), so its columns can be used in field objects and formulas. " +
                "Crystal connects to read the column list, so the server must be reachable; the columns are returned. " +
                "The new table is not linked to existing tables. To repoint tables that are already in the report, use set_datasource.",
                Schema(new[] { PathArg, Req("table", "string", "Table name, optionally with schema: 'Orders' or 'dbo.Orders' (schema defaults to dbo)."),
                               Opt("alias", "string", "Alias used in field names, {Alias.Column} (default: the table name)."),
                               Opt("provider", "string", "OLE DB provider (default MSOLEDBSQL; the legacy SQLOLEDB cannot reach LocalDB or TLS 1.2-only servers)."),
                               SubArg, OutArg }
                       .Concat(LogonArgs("server, database and credentials; integrated security when no user is given")).ToArray()),
                AddTable);

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

            r.Add("set_text_with_fields",
                "Set a text object's text with fields embedded in it, e.g. \"Shift : {Orders.Shift}\", \"Page {PageNumber} of {TotalPageCount}\" or " +
                "\"Period: {?Start} - {?End}\". Every {...} must be a field ({Table.Column}, {@Formula}, {?Param}, {#RunningTotal}) or a special field. " +
                "Give object to change an existing text object (its font is kept), or section + position to create one. Format the embedded fields with set_field_format + field.",
                Schema(PathArg, Req("text", "string", "Text with {field} placeholders."),
                       Opt("object", "string", "Existing text object to change."),
                       Opt("section", "string", "Section for a new text object."),
                       Opt("left", "integer", "New object: left (twips)."), Opt("top", "integer", "New object: top (twips)."),
                       Opt("width", "integer", "New object: width (twips)."), Opt("height", "integer", "New object: height (twips)."),
                       Opt("name", "string", "New object: name."), SubArg, OutArg),
                SetTextWithFields);

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

            r.Add("add_section",
                "Add a section to an area, e.g. a second Detail section or an extra Group Footer. Group header/footer areas come from add_group.",
                Schema(PathArg, Enum("kind", "Area kind; when there are several (e.g. two group footers) pass area instead.", false,
                                     "report_header", "page_header", "group_header", "detail", "group_footer", "page_footer", "report_footer"),
                       Opt("area", "string", "Area name (e.g. DetailArea1), or the name of a section in it; overrides kind."),
                       Opt("index", "integer", "Position within the area, 0 = first (default: last)."),
                       Opt("height", "integer", "Height in twips (default 300)."), SubArg, OutArg),
                AddSection);

            r.Add("delete_section",
                "Delete a section. Refused while it holds objects (or lines/boxes end in it), unless force=true. The only section of an area cannot be deleted; suppress it instead.",
                Schema(PathArg, Req("section", "string", "Section name."),
                       Opt("force", "boolean", "Delete its objects too."), SubArg, OutArg),
                DeleteSection);

            r.Add("move_object",
                "Move a report object to another section, keeping its name, size, font, formatting and conditional formulas (position too unless left/top are given).",
                Schema(PathArg, Req("object", "string", "Object name."), Req("section", "string", "Target section."),
                       Opt("left", "integer", "New left (twips)."), Opt("top", "integer", "New top (twips)."), SubArg, OutArg),
                MoveObject);

            r.Add("set_page_setup",
                "Set paper size, orientation and margins of the report (also used when exporting to PDF). Returns the printable width/height to lay out objects with.",
                Schema(PathArg,
                       Enum("size", "Paper size.", false, "A3", "A4", "A5", "B4", "B5", "Letter", "Legal", "Tabloid", "Executive", "Folio"),
                       Enum("orientation", "Orientation (default: keep).", false, "portrait", "landscape"),
                       Opt("width", "integer", "Custom paper width in twips (with height, instead of size)."),
                       Opt("height", "integer", "Custom paper height in twips."),
                       Opt("margin_left", "integer", "Left margin (twips)."), Opt("margin_right", "integer", "Right margin (twips)."),
                       Opt("margin_top", "integer", "Top margin (twips)."), Opt("margin_bottom", "integer", "Bottom margin (twips)."),
                       OutArg),
                SetPageSetup);

            r.Add("add_subreport",
                "Insert another .rpt as a subreport (Crystal copies it into this report). The runtime cannot create an empty report, so build the " +
                "subreport as its own .rpt first (e.g. copy a report and trim it with these tools). Link it with links / set_subreport_links so it does not prompt.",
                Schema(PathArg, Req("section", "string", "Section of the main report, e.g. ReportFooterSection1 or a group footer."),
                       Req("source_path", "string", "The .rpt to import."),
                       Opt("name", "string", "Subreport name (default: the file name)."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Req("width", "integer", "Width (twips)."), Req("height", "integer", "Height (twips); the section grows to fit."),
                       Opt("links", "object[]", "Links, as for set_subreport_links."), OutArg),
                AddSubreport);

            r.Add("set_subreport_links",
                "Link a subreport to the main report so it gets its values from there instead of prompting. Each link is {\"main\": ..., \"sub\": ...}: " +
                "a main parameter to a subreport parameter ({?Start} -> {?Start}), or a main field/formula to a subreport field ({Orders.Customer} -> {Lines.Customer}), " +
                "which filters the subreport to the main record's value (e.g. in a group footer: only that group's rows). sub defaults to the same name. " +
                "Existing links are kept unless replace=true; links [] with replace=true removes them all.",
                Schema(PathArg, Req("subreport", "string", "Subreport name."),
                       Req("links", "object[]", "Links: [{\"main\": \"{?Start}\", \"sub\": \"{?Start}\"}, {\"main\": \"{Orders.Customer}\"}]."),
                       Opt("replace", "boolean", "Replace all existing links (default: add/update)."), OutArg),
                SetSubreportLinks);

            r.Add("add_text_object", "Add a text object to a section. Use set_object_props afterwards for font/color/alignment.",
                Schema(PathArg, Req("section", "string", "Section name."), Req("text", "string", "Text."),
                       Req("left", "integer", "Left (twips)."), Req("top", "integer", "Top (twips)."),
                       Req("width", "integer", "Width (twips)."), Req("height", "integer", "Height (twips)."),
                       Opt("name", "string", "Object name (default: assigned by Crystal)."), SubArg, OutArg),
                AddTextObject);

            r.Add("add_field_object",
                "Add a field object bound to a database field, formula, parameter, SQL expression or running total, e.g. {Orders.OrderNo}, {@Total}, {?StartDate}, " +
                "or to a special field: RecordNumber, GroupNumber, PageNumber, TotalPageCount, PageNofM, PrintDate, PrintTime, ModificationDate, ModificationTime, " +
                "DataDate, DataTime, ReportTitle, ReportComments, FileName, FileAuthor, FileCreationDate, RecordSelection, GroupSelection.",
                Schema(PathArg, Req("section", "string", "Section name."), Req("field", "string", "Field in formula form, e.g. {Table.Column}, or a special field name, e.g. RecordNumber."),
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
            r.Add("verify_database",
                "Check that the report still matches its database (Crystal's Verify Database), e.g. after set_datasource or a schema change: " +
                "reports columns the report uses that no longer exist, changed column types, objects Crystal would delete and logon problems. Never changes the file.",
                Schema(new[] { PathArg }.Concat(LogonArgs("DB logon used for the check")).ToArray()),
                VerifyDatabase);

            r.Add("export_report",
                "Run the report against the database and export it. To visually check a layout change, export to png and open the image: " +
                "a single page is written to output_path, several pages to <name>-<page>.png next to it. " +
                "Parameter values: {\"Name\": value} or {\"Name\": [v1, v2]}; for an unlinked subreport parameter use \"Subreport::Name\".",
                Schema(new[]
                {
                    PathArg, Req("output_path", "string", "Output file."),
                    Enum("format", "Export format (default pdf). png/jpg render the pages as images.", false,
                         "pdf", "xlsx", "xls", "xls_data", "doc", "rtf", "csv", "txt", "rpt", "png", "jpg"),
                    Opt("dpi", "integer", "png/jpg only: resolution, 36-600 (default 150)."),
                    Opt("pages", "string", "png/jpg only: pages to render, e.g. \"1\", \"1-3\" or \"1,3\" (default all)."),
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
