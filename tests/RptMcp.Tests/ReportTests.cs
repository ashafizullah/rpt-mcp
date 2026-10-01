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
                var (_, text) = FirstTextObject(c.CallOk("inspect_report", new JObject { ["path"] = _report }));
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

        /// <summary>Exports to CSV, giving every prompting parameter its first default value (or a value of its type).</summary>
        private void ExportCsv(McpClient c, string csv)
        {
            var values = new JObject();
            foreach (var p in ((JObject)c.CallOk("inspect_report", new JObject { ["path"] = _report, ["include_objects"] = false })["parameters"]).Properties())
            {
                var def = p.Value["defaults"]?.FirstOrDefault();
                switch ((string)p.Value["type"])
                {
                    case "date": case "datetime": values[p.Name] = def ?? "2000-01-01"; break;
                    case "number": case "currency": values[p.Name] = def ?? "0"; break;
                    case "boolean": values[p.Name] = def ?? "false"; break;
                    default: values[p.Name] = def ?? ""; break;
                }
            }
            c.CallOk("export_report", new JObject { ["path"] = _report, ["format"] = "csv", ["output_path"] = csv, ["parameters"] = values });
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
                Assert.Equal(field, (string)inspected["groups"].Single());
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
                var res = c.CallOk("verify_database", new JObject { ["path"] = _report });
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
