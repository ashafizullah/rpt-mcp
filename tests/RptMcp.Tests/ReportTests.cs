using System;
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
    }
}
