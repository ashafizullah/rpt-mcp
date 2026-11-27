using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RptMcp.Tests
{
    /// <summary>
    /// Edits a copy of a real report. Set RPTMCP_TEST_REPORT to any .rpt that has at least one text object;
    /// without it these tests are skipped (the repository ships no .rpt fixture yet).
    /// </summary>
    public sealed class ReportTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _report;

        public ReportTests()
        {
            var source = Environment.GetEnvironmentVariable("RPTMCP_TEST_REPORT");
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            _dir = Path.Combine(Path.GetTempPath(), "rptmcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _report = Path.Combine(_dir, "report.rpt");
            File.Copy(source, _report);
        }

        public void Dispose()
        {
            if (_dir != null) try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }

        private void RequireReport() => Skip.If(_report == null, "Set RPTMCP_TEST_REPORT to a .rpt file to run report tests.");

        private static string Hash(string path)
        {
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(path)));
        }

        private static (string Section, string Text) FirstTextObject(JObject inspect)
        {
            foreach (var s in ((JObject)inspect["sections"]).Properties())
                foreach (var o in ((JObject)s.Value["objects"] ?? new JObject()).Properties())
                    if ((string)o.Value["kind"] == "Text") return (s.Name, o.Name);
            throw new SkipException("The test report has no text object.");
        }

        /// <summary>A text object without embedded fields: changing its text changes nothing else.</summary>
        private static (string Section, string Text) FirstPlainTextObject(JObject inspect)
        {
            foreach (var s in ((JObject)inspect["sections"]).Properties())
                foreach (var o in ((JObject)s.Value["objects"] ?? new JObject()).Properties())
                    if ((string)o.Value["kind"] == "Text" && ((JObject)(o.Value["embedded_fields"] ?? new JObject())).Count == 0)
                        return (s.Name, o.Name);
            throw new SkipException("The test report has no text object without embedded fields.");
        }

        [SkippableFact]
        public void Inspect_describes_sections_and_objects()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var r = c.CallOk("inspect_report", new JObject { ["path"] = _report });
                Assert.NotEmpty((JObject)r["sections"]);
                Assert.NotNull(r["page"]);
            }
        }

        [SkippableFact]
        public void Edit_to_output_path_leaves_source_untouched()
        {
            RequireReport();
            var before = Hash(_report);
            var copy = Path.Combine(_dir, "copy.rpt");
            using (var c = new McpClient())
            {
                var (_, text) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                c.CallOk("set_text", new JObject { ["path"] = _report, ["object"] = text, ["text"] = "rpt-mcp test", ["output_path"] = copy });

                Assert.Equal(before, Hash(_report));
                Assert.False(Directory.Exists(Path.Combine(_dir, "_rptmcp_backup")));
                var reloaded = c.CallOk("inspect_report", new JObject { ["path"] = copy });
                var obj = ((JObject)reloaded["sections"]).Properties().SelectMany(s => ((JObject)s.Value["objects"]).Properties()).Single(o => o.Name == text);
                Assert.Equal("rpt-mcp test", (string)obj.Value["text"]);
            }
        }

        [SkippableFact]
        public void Overwrite_makes_backup_and_diff_shows_only_the_change()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                // A text with embedded fields loses them when its text is replaced, so the diff
                // would report two changes; take a plain text object.
                var (_, text) = FirstPlainTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                var res = c.CallOk("set_text", new JObject { ["path"] = _report, ["object"] = text, ["text"] = "changed" });
                var backup = (string)res["backup"];
                Assert.True(File.Exists(backup));

                var diff = c.CallOk("diff_reports", new JObject { ["path_a"] = backup, ["path_b"] = _report });
                Assert.Equal(1, (int)diff["changes"]);
                Assert.EndsWith($".{text}.text", (string)diff["diff"][0]["changed"]);
            }
        }

        [SkippableFact]
        public void Batch_edit_is_all_or_nothing()
        {
            RequireReport();
            var before = Hash(_report);
            using (var c = new McpClient())
            {
                var (_, text) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                var res = c.Call("batch_edit", new JObject
                {
                    ["path"] = _report,
                    ["operations"] = new JArray(
                        new JObject { ["tool"] = "set_text", ["object"] = text, ["text"] = "first" },
                        new JObject { ["tool"] = "set_text", ["object"] = "NoSuchObject", ["text"] = "second" })
                });
                Assert.True(res.IsError);
                Assert.Contains("Operation 2", res.Text);
                Assert.Equal(before, Hash(_report));
                Assert.False(Directory.Exists(Path.Combine(_dir, "_rptmcp_backup")));
            }
        }

        [SkippableFact]
        public void Deleting_a_used_formula_is_refused_unless_forced()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var (section, _) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                c.CallOk("batch_edit", new JObject
                {
                    ["path"] = _report,
                    ["operations"] = new JArray(
                        new JObject { ["tool"] = "set_formula", ["name"] = "RptMcpTest", ["text"] = "\"x\"" },
                        new JObject { ["tool"] = "add_field_object", ["section"] = section, ["field"] = "{@RptMcpTest}", ["left"] = 0, ["top"] = 0, ["width"] = 500, ["height"] = 200, ["name"] = "RptMcpField" })
                });

                var refused = c.Call("delete_formula", new JObject { ["path"] = _report, ["name"] = "RptMcpTest" });
                Assert.True(refused.IsError);
                Assert.Contains("RptMcpField", refused.Text);

                var forced = c.CallOk("delete_formula", new JObject { ["path"] = _report, ["name"] = "RptMcpTest", ["force"] = true });
                Assert.Contains("RptMcpField", forced["removed_objects"].Values<string>());
            }
        }

        [SkippableFact]
        public void New_field_object_can_be_restyled_in_the_same_batch()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var (section, _) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                var res = c.CallOk("batch_edit", new JObject
                {
                    ["path"] = _report,
                    ["operations"] = new JArray(
                        new JObject { ["tool"] = "set_formula", ["name"] = "RptMcpTest", ["text"] = "\"x\"" },
                        new JObject { ["tool"] = "add_field_object", ["section"] = section, ["field"] = "{@RptMcpTest}", ["left"] = 0, ["top"] = 0, ["width"] = 500, ["height"] = 200, ["name"] = "RptMcpField" },
                        new JObject { ["tool"] = "set_object_props", ["object"] = "RptMcpField", ["left"] = 100, ["bold"] = true })
                });
                Assert.Equal("Arial 10pt bold", (string)res["operations"][2]["result"]["now"]["font"]);
            }
        }

        private static JObject FindObject(JObject inspect, string name) =>
            ((JObject)inspect["sections"]).Properties()
                .Select(s => new { s.Name, Obj = (JObject)((JObject)s.Value["objects"] ?? new JObject())[name] })
                .Where(x => x.Obj != null).Select(x => { x.Obj["section"] = x.Name; return x.Obj; }).SingleOrDefault();

        [SkippableFact]
        public void Special_fields_and_text_with_embedded_fields()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var (section, text) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                c.CallOk("batch_edit", new JObject
                {
                    ["path"] = _report,
                    ["operations"] = new JArray(
                        new JObject { ["tool"] = "add_field_object", ["section"] = section, ["field"] = "RecordNumber", ["left"] = 0, ["top"] = 0, ["width"] = 500, ["height"] = 200, ["name"] = "RptMcpRecNo" },
                        new JObject { ["tool"] = "set_text_with_fields", ["object"] = text, ["text"] = "Page {PageNumber} of {TotalPageCount}" })
                });
                var inspected = c.CallOk("inspect_report", new JObject { ["path"] = _report });
                var recNo = FindObject(inspected, "RptMcpRecNo");
                Assert.Equal("RecordNumber", (string)recNo["field"]);
                Assert.True((bool)recNo["special"]);
                var embedded = (JObject)FindObject(inspected, text)["embedded_fields"];
                Assert.Equal(new[] { "PageNumber", "TotalPageCount" }, embedded.Properties().Select(p => p.Name));

                var bad = c.Call("set_text_with_fields", new JObject { ["path"] = _report, ["object"] = text, ["text"] = "{NoSuch.Field}" });
                Assert.True(bad.IsError);
                Assert.Contains("not found", bad.Text);
            }
        }

        [SkippableFact]
        public void Page_setup_changes_paper_orientation_and_margins()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var res = c.CallOk("set_page_setup", new JObject
                {
                    ["path"] = _report, ["size"] = "A4", ["orientation"] = "landscape", ["margin_left"] = 500, ["margin_right"] = 500
                });
                Assert.Equal(16838 - 1000, (int)res["page_content_width"]);
                var page = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false })["page"];
                Assert.Equal("PaperA4", (string)page["paper_size"]);
                Assert.Equal("Landscape", (string)page["orientation"]);
                Assert.Equal(500, (int)page["margins_twips"]["left"]);
            }
        }

        [SkippableFact]
        public void Set_parameter_changes_prompt_and_defaults_in_place()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                c.CallOk("add_parameter", new JObject { ["path"] = _report, ["name"] = "RptMcpParam", ["prompt"] = "Old", ["default_values"] = new JArray("a") });
                var res = c.CallOk("set_parameter", new JObject { ["path"] = _report, ["name"] = "{?RptMcpParam}", ["prompt"] = "New", ["default_values"] = new JArray("x", "y") });
                Assert.Equal(new[] { "prompt", "default_values" }, res["changed"].Values<string>());
                var p = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false })["parameters"]["RptMcpParam"];
                Assert.Equal("New", (string)p["prompt"]);
                Assert.Equal(new[] { "x", "y" }, p["defaults"].Values<string>());
                Assert.True(c.Call("set_parameter", new JObject { ["path"] = _report, ["name"] = "RptMcpParam" }).IsError);
            }
        }

        [SkippableFact]
        public void Add_section_move_object_and_guarded_delete_section()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var (section, text) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                var added = c.CallOk("add_section", new JObject { ["path"] = _report, ["area"] = section, ["height"] = 500 });
                var newSection = (string)added["section"];

                var moved = c.CallOk("move_object", new JObject { ["path"] = _report, ["object"] = text, ["section"] = newSection, ["top"] = 0 });
                Assert.Equal(section, (string)moved["from_section"]);
                var obj = FindObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }), text);
                Assert.Equal(newSection, (string)obj["section"]);

                var refused = c.Call("delete_section", new JObject { ["path"] = _report, ["section"] = newSection });
                Assert.True(refused.IsError);
                Assert.Contains(text, refused.Text);
                var deleted = c.CallOk("delete_section", new JObject { ["path"] = _report, ["section"] = newSection, ["force"] = true });
                Assert.Contains(text, deleted["removed_objects"].Values<string>());
            }
        }

        [SkippableFact]
        public void Add_subreport_links_parameters_and_accepts_subreport_edits()
        {
            RequireReport();
            var source = Path.Combine(_dir, "source.rpt");
            File.Copy(_report, source);
            using (var c = new McpClient())
            {
                var inspected = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false });
                var section = ((JObject)inspected["sections"]).Properties().First(s => (string)s.Value["kind"] == "ReportFooter").Name;
                var mainParams = ((JObject)inspected["parameters"]).Properties().Select(p => "{?" + p.Name + "}").ToList();

                var added = c.CallOk("add_subreport", new JObject
                {
                    ["path"] = _report, ["section"] = section, ["source_path"] = source, ["name"] = "RptMcpSub",
                    ["left"] = 0, ["top"] = 0, ["width"] = 3000, ["height"] = 400,
                    ["links"] = new JArray(mainParams.Select(p => new JObject { ["main"] = p }))
                });
                Assert.Equal("RptMcpSub", (string)added["subreport"]);
                Assert.True(c.Call("add_subreport", new JObject
                {
                    ["path"] = _report, ["section"] = section, ["source_path"] = source, ["name"] = "RptMcpSub",
                    ["left"] = 0, ["top"] = 0, ["width"] = 3000, ["height"] = 400
                }).IsError);

                var after = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false });
                Assert.Contains("RptMcpSub", after["subreports"].Values<string>());
                Assert.Equal(mainParams.Count, ((JArray)after["subreport_links"]?["RptMcpSub"] ?? new JArray()).Count);

                // Parameters inside a subreport need the subreport's name (this used to fail).
                c.CallOk("add_parameter", new JObject { ["path"] = _report, ["subreport"] = "RptMcpSub", ["name"] = "RptMcpSubParam" });
                var sub = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["subreport"] = "RptMcpSub", ["include_objects"] = false });
                Assert.NotNull(sub["parameters"]["RptMcpSubParam"]);

                var obj = (string)added["object"];
                var move = c.Call("move_object", new JObject { ["path"] = _report, ["object"] = obj, ["section"] = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report })).Section });
                Assert.True(move.IsError);
                Assert.Contains("subreport", move.Text);

                var cleared = c.CallOk("set_subreport_links", new JObject { ["path"] = _report, ["subreport"] = "RptMcpSub", ["links"] = new JArray(), ["replace"] = true });
                Assert.Empty((JArray)cleared["links"]);
            }
        }

        [SkippableFact]
        public void Condition_formula_on_a_section_round_trips()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var section = ((JObject)c.CallOk("inspect_report", new JObject { ["path"] = _report })["sections"]).Properties().First().Name;
                var r = c.CallOk("set_condition_formula", new JObject { ["path"] = _report, ["section"] = section, ["condition"] = "Format.EnableSuppress", ["formula"] = "PageNumber > 1" });
                Assert.Equal(section, (string)r["section"]);
                var conds = c.CallOk("inspect_report", new JObject { ["path"] = _report })["sections"][section]["conditions"];
                Assert.Equal("PageNumber > 1", (string)conds["Format.EnableSuppress"]);

                c.CallOk("set_condition_formula", new JObject { ["path"] = _report, ["section"] = section, ["condition"] = "Format.EnableSuppress", ["formula"] = "" });
                Assert.Null(c.CallOk("inspect_report", new JObject { ["path"] = _report })["sections"][section]["conditions"]?["Format.EnableSuppress"]);

                // A set_formula without text must not silently create an empty formula.
                var (isError, _) = c.Call("set_formula", new JObject { ["path"] = _report, ["name"] = "RptMcpNoText", ["formula"] = "1" });
                Assert.True(isError);
            }
        }

        [SkippableFact]
        public void Export_to_png_and_jpg_renders_pages()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var png = Path.Combine(_dir, "page.png");
                var first = c.Call("export_report", new JObject { ["path"] = _report, ["format"] = "png", ["output_path"] = png, ["pages"] = "1", ["parameters"] = DefaultParameters(c) });
                if (first.IsError) SkipIfDatabaseUnreachable(first.Text);
                Assert.True(!first.IsError, first.Text);
                var r = JObject.Parse(first.Text);
                Assert.Equal(png, (string)((JArray)r["exported"]).Single());
                var bytes = File.ReadAllBytes(png);
                Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes.Take(4));
                var width = (int)r["pages"][0]["width_px"];

                var hi = c.CallOk("export_report", new JObject { ["path"] = _report, ["format"] = "png", ["output_path"] = Path.Combine(_dir, "hi.png"), ["pages"] = "1", ["dpi"] = 300, ["parameters"] = DefaultParameters(c) });
                Assert.InRange((int)hi["pages"][0]["width_px"], width * 2 - 2, width * 2 + 2);

                var jpg = Path.Combine(_dir, "page.jpg");
                c.CallOk("export_report", new JObject { ["path"] = _report, ["format"] = "jpg", ["output_path"] = jpg, ["pages"] = "1", ["parameters"] = DefaultParameters(c) });
                Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(jpg).Take(2));

                // All pages: one file per page, named <name>-<page>.png.
                var all = c.CallOk("export_report", new JObject { ["path"] = _report, ["format"] = "png", ["output_path"] = Path.Combine(_dir, "all.png"), ["parameters"] = DefaultParameters(c) });
                var total = (int)all["total_pages"];
                Assert.Equal(total, ((JArray)all["exported"]).Count);
                if (total > 1) Assert.True(File.Exists(Path.Combine(_dir, "all-2.png")));

                var (isError, text) = c.Call("export_report", new JObject { ["path"] = _report, ["format"] = "png", ["output_path"] = png, ["pages"] = "999", ["parameters"] = DefaultParameters(c) });
                Assert.True(isError);
                Assert.Contains("out of range", text);
            }
        }

        /// <summary>
        /// Needs a reachable SQL Server: RPTMCP_TEST_DB_SERVER and RPTMCP_TEST_DB_DATABASE
        /// (Windows integrated security), e.g. (localdb)\MSSQLLocalDB / rptmcp.
        /// </summary>
        [SkippableFact]
        public void Sql_command_table_can_be_added_and_its_sql_replaced()
        {
            RequireReport();
            string server = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_SERVER");
            string database = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_DATABASE");
            Skip.If(string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database),
                "Set RPTMCP_TEST_DB_SERVER and RPTMCP_TEST_DB_DATABASE to run the SQL command test.");
            const string alias = "RptMcpCmd";

            using (var c = new McpClient())
            {
                var added = c.CallOk("add_table", new JObject
                {
                    ["path"] = _report, ["table"] = alias, ["alias"] = alias,
                    ["sql"] = "SELECT 1 AS A", ["server"] = server, ["database"] = database, ["integrated"] = true
                });
                Assert.True((bool)added["command"]);
                Assert.Contains("SELECT 1 AS A", (string)added["sql"]);
                Assert.NotEmpty(added["fields"].Values<string>());

                var changed = c.CallOk("set_command_sql", new JObject
                {
                    ["path"] = _report, ["table"] = alias, ["sql"] = "SELECT 2 AS B",
                    ["server"] = server, ["database"] = database, ["integrated"] = true
                });
                Assert.Equal(alias, (string)changed["table"]);
                Assert.Contains("SELECT 1 AS A", (string)changed["old_sql"]);

                var inspected = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_fields"] = true });
                Assert.Equal("command", (string)inspected["tables"][alias]["type"]);
                Assert.Contains("SELECT 2 AS B", (string)inspected["tables"][alias]["sql"]);
            }
        }

        /// <summary>Exports to CSV, giving every prompting parameter its first default value (or a value of its type).</summary>
        private void ExportCsv(McpClient c, string csv)
        {
            var res = c.Call("export_report", new JObject { ["path"] = _report, ["format"] = "csv", ["output_path"] = csv, ["parameters"] = DefaultParameters(c) });
            if (res.IsError) SkipIfDatabaseUnreachable(res.Text);
            Assert.True(!res.IsError, res.Text);
        }

        /// <summary>The links of the report as "Source.Field Operator Target.Field".</summary>
        private List<string> Links(McpClient c) =>
            ((JArray)c.CallOk("inspect_report", new JObject { ["path"] = _report })["links"] ?? new JArray())
                .Select(l => (string)l).ToList();

        /// <summary>
        /// Skips the test when the database of the test report cannot be reached: that is the
        /// environment, not the tool. The message of the runtime is localized, so it is matched loosely.
        /// </summary>
        private static void SkipIfDatabaseUnreachable(string text)
        {
            var t = text ?? "";
            if (t.IndexOf("conectar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("LogOnException", StringComparison.OrdinalIgnoreCase) >= 0)
                Skip.If(true, "The database of the test report is not reachable: " + t);
        }

        [SkippableFact]
        public void Table_link_can_be_changed_to_an_outer_join_and_removed_again()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var links = Links(c);
                Skip.If(links.Count == 0, "The test report has no table link.");
                var parts = links[0].Split(' ');
                var source = parts[0];
                var target = parts[2];

                var inner = c.CallOk("set_table_link", new JObject { ["path"] = _report, ["source"] = source, ["target"] = target, ["join"] = "inner" });
                Assert.Equal("inner", (string)inner["join"]);
                Assert.True((bool)inner["removed"] == false);

                // An outer join on a table that is reachable by two paths needs the redundant
                // path to be dropped, otherwise Crystal builds an invalid query.
                var left = c.CallOk("set_table_link", new JObject { ["path"] = _report, ["source"] = source, ["target"] = target, ["join"] = "left", ["drop_conflicts"] = true });
                Assert.Equal("left", (string)left["join"]);
                Assert.NotNull(left["conflicting_links"]);
                Assert.NotNull(left["dropped_links"]);
                Assert.Contains(Links(c), l => l.Contains(source + " LeftOuterJoin " + target));

                var none = c.CallOk("set_table_link", new JObject { ["path"] = _report, ["source"] = source, ["target"] = target, ["join"] = "none" });
                Assert.True((bool)none["removed"]);
                Assert.DoesNotContain(Links(c), l => l.StartsWith(source + " ") && l.EndsWith(" " + target));
            }
        }

        [SkippableFact]
        public void Export_names_the_subreport_parameter_that_is_missing()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var details = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_subreports"] = true })["subreport_details"] as JObject;
                Skip.If(details == null, "The test report has no subreport.");
                var missing = details.Properties()
                    .Select(p => new { Sub = p.Name, Param = ((JObject)p.Value["parameters"] ?? new JObject()).Properties().FirstOrDefault(x => x.Value["linked"]?.Value<bool>() != true) })
                    .FirstOrDefault(x => x.Param != null);
                Skip.If(missing == null, "The test report has no unlinked subreport parameter.");

                // the parameters of the subreport are left out on purpose: the export has to fail
                var res = c.Call("export_report", new JObject { ["path"] = _report, ["format"] = "png", ["output_path"] = Path.Combine(_dir, "x.png"), ["parameters"] = MainParameters(c) });
                Assert.True(res.IsError);
                // the error has to say which subreport and which parameter are missing
                Assert.Contains(missing.Sub + "::" + missing.Param.Name, res.Text);
            }
        }

        private JObject DefaultParameters(McpClient c)
        {
            var values = MainParameters(c);
            // A parameter of a subreport is given as "Subreport::Name"; a linked one takes its
            // value from the main report and needs nothing.
            var details = (JObject)(c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_subreports"] = true })["subreport_details"] ?? new JObject());
            foreach (var sub in details.Properties())
                foreach (var p in ((JObject)(sub.Value["parameters"] ?? new JObject())).Properties())
                    if (p.Value["linked"]?.Value<bool>() != true)
                        values[sub.Name + "::" + p.Name] = DefaultValue(p.Value);
            return values;
        }

        /// <summary>The parameters of the report itself, without the ones of its subreports.</summary>
        private JObject MainParameters(McpClient c)
        {
            var values = new JObject();
            foreach (var p in ((JObject)c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false })["parameters"]).Properties())
                values[p.Name] = DefaultValue(p.Value);
            return values;
        }

        /// <summary>The first default value of a parameter, or a value of its type.</summary>
        private static JToken DefaultValue(JToken parameter)
        {
            var def = parameter["defaults"]?.FirstOrDefault();
            switch ((string)parameter["type"])
            {
                case "date": case "datetime": return def ?? "2000-01-01";
                case "number": case "currency": return def ?? "0";
                case "boolean": return def ?? "false";
                default: return def ?? "";
            }
        }

        /// <summary>Adds the RPTMCP_TEST_DB_* table to the report and returns its fields as {Alias.Column}:Type.</summary>
        private List<string> AddTestTable(McpClient c, string alias)
        {
            string server = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_SERVER"),
                   database = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_DATABASE"),
                   table = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_TABLE");
            Skip.If(string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(table),
                "Set RPTMCP_TEST_DB_SERVER, RPTMCP_TEST_DB_DATABASE and RPTMCP_TEST_DB_TABLE to run database tests.");
            var added = c.CallOk("add_table", new JObject
            {
                ["path"] = _report, ["table"] = table, ["alias"] = alias, ["server"] = server, ["database"] = database, ["integrated"] = true
            });
            return added["fields"].Values<string>().ToList();
        }

        [SkippableFact]
        public void Group_sort_and_running_total_round_trip()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                var fields = AddTestTable(c, "RptMcpTable");
                var field = fields[0].Substring(0, fields[0].LastIndexOf(':'));

                var group = c.CallOk("add_group", new JObject { ["path"] = _report, ["field"] = field, ["direction"] = "descending" });
                string header = (string)group["group_header_section"], footer = (string)group["group_footer_section"];
                Assert.False(string.IsNullOrEmpty(header));
                Assert.False(string.IsNullOrEmpty(footer));
                Assert.True(c.Call("add_group", new JObject { ["path"] = _report, ["field"] = field }).IsError);

                c.CallOk("add_running_total", new JObject
                {
                    ["path"] = _report, ["name"] = "RptMcpCount", ["field"] = field, ["operation"] = "count",
                    ["reset"] = "on_change_of_group", ["reset_on"] = field
                });
                var res = c.CallOk("batch_edit", new JObject
                {
                    ["path"] = _report,
                    ["operations"] = new JArray(
                        new JObject { ["tool"] = "set_text_with_fields", ["section"] = header, ["text"] = "Group: " + field, ["left"] = 0, ["top"] = 0, ["width"] = 3000, ["height"] = 200 },
                        new JObject { ["tool"] = "add_field_object", ["section"] = footer, ["field"] = "{#RptMcpCount}", ["left"] = 0, ["top"] = 0, ["width"] = 1000, ["height"] = 200, ["name"] = "RptMcpRt" },
                        new JObject { ["tool"] = "add_sort", ["field"] = field, ["direction"] = "ascending" })
                });
                Assert.Equal("{@RptMcpCount}", (string)res["operations"][1]["result"]["via_formula"]);
                Assert.True((bool)res["operations"][2]["result"]["group_sort"]);

                var inspected = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false });
                // the report may already have groups of its own
                Assert.Contains(field, inspected["groups"].Values<string>());
                Assert.Contains("Count of", (string)inspected["running_totals"]["RptMcpCount"]);

                var csv = Path.Combine(_dir, "group.csv");
                ExportCsv(c, csv);
                Assert.NotEmpty(File.ReadAllLines(csv));

                var refused = c.Call("delete_group", new JObject { ["path"] = _report, ["field"] = field });
                Assert.True(refused.IsError);
                Assert.Contains("RptMcpRt", refused.Text);
                c.CallOk("delete_running_total", new JObject { ["path"] = _report, ["name"] = "RptMcpCount", ["force"] = true });
                var deleted = c.CallOk("delete_group", new JObject { ["path"] = _report, ["field"] = field, ["force"] = true });
                Assert.Empty((JArray)deleted["groups"]);
            }
        }

        [SkippableFact]
        public void Verify_database_passes_for_a_reachable_table_and_never_saves()
        {
            RequireReport();
            using (var c = new McpClient())
            {
                AddTestTable(c, "RptMcpTable");
                var before = Hash(_report);
                var call = c.Call("verify_database", new JObject { ["path"] = _report });
                if (call.IsError) SkipIfDatabaseUnreachable(call.Text);
                Assert.True(!call.IsError, call.Text);
                var res = JObject.Parse(call.Text);
                // the report may point at a database this machine cannot reach
                if (!(bool)res["verified"]) SkipIfDatabaseUnreachable(res.ToString());
                Assert.True((bool)res["verified"], res.ToString());
                Assert.Equal(before, Hash(_report));
            }
        }

        /// <summary>
        /// Needs a reachable SQL Server table: RPTMCP_TEST_DB_SERVER, RPTMCP_TEST_DB_DATABASE and RPTMCP_TEST_DB_TABLE
        /// (Windows integrated security), e.g. (localdb)\MSSQLLocalDB / rptmcp / dbo.ProductionReport.
        /// </summary>
        [SkippableFact]
        public void Add_table_makes_its_columns_usable_and_exports_rows()
        {
            RequireReport();
            string server = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_SERVER"),
                   database = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_DATABASE"),
                   table = Environment.GetEnvironmentVariable("RPTMCP_TEST_DB_TABLE");
            Skip.If(string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(table),
                "Set RPTMCP_TEST_DB_SERVER, RPTMCP_TEST_DB_DATABASE and RPTMCP_TEST_DB_TABLE to run the add_table test.");
            const string alias = "RptMcpTable";

            using (var c = new McpClient())
            {
                var (section, _) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
                var added = c.CallOk("add_table", new JObject
                {
                    ["path"] = _report, ["table"] = table, ["alias"] = alias,
                    ["server"] = server, ["database"] = database, ["integrated"] = true
                });
                var fields = added["fields"].Values<string>().ToList();
                Assert.NotEmpty(fields);
                Assert.All(fields, f => Assert.StartsWith("{" + alias + ".", f));

                var again = c.Call("add_table", new JObject
                {
                    ["path"] = _report, ["table"] = table, ["alias"] = alias,
                    ["server"] = server, ["database"] = database, ["integrated"] = true
                });
                Assert.True(again.IsError);
                Assert.Contains("already has a table", again.Text);

                var inspected = c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_fields"] = true });
                Assert.Equal(fields.Count, ((JArray)inspected["tables"][alias]["fields"]).Count);

                var firstField = fields[0].Substring(0, fields[0].LastIndexOf(':'));
                c.CallOk("add_field_object", new JObject
                {
                    ["path"] = _report, ["section"] = section, ["field"] = firstField,
                    ["left"] = 0, ["top"] = 0, ["width"] = 1000, ["height"] = 200
                });

                var csv = Path.Combine(_dir, "out.csv");
                // No logon arguments: the connection saved by add_table must be enough.
                ExportCsv(c, csv);
                Assert.True(File.Exists(csv));
                Assert.NotEmpty(File.ReadAllLines(csv));
            }
        }
    }
}
