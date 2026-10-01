using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RptMcp.Tests
{
    /// <summary>Protocol and validation behaviour; needs the Crystal runtime but no .rpt file.</summary>
    public class ProtocolTests
    {
        [Fact]
        public void Initialize_reports_server_info_and_echoes_protocol_version()
        {
            using (var c = new McpClient())
            {
                var result = (JObject)c.Initialize["result"];
                Assert.Equal("rpt-mcp", (string)result["serverInfo"]["name"]);
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(McpClient.ServerPath);
                Assert.Equal($"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}", (string)result["serverInfo"]["version"]);
                Assert.Equal("2025-06-18", (string)result["protocolVersion"]);
                Assert.NotNull(result["capabilities"]["tools"]);
            }
        }

        [Fact]
        public void Tools_list_has_valid_schemas()
        {
            using (var c = new McpClient())
            {
                var tools = (JArray)c.Request("tools/list")["result"]["tools"];
                Assert.True(tools.Count >= 25, $"expected at least 25 tools, got {tools.Count}");
                foreach (JObject t in tools)
                {
                    var name = (string)t["name"];
                    Assert.False(string.IsNullOrWhiteSpace((string)t["description"]), name);
                    var schema = (JObject)t["inputSchema"];
                    Assert.Equal("object", (string)schema["type"]);
                    var props = (JObject)schema["properties"];
                    foreach (var req in schema["required"] ?? new JArray())
                        Assert.True(props[(string)req] != null, $"{name}: required '{req}' is not a property");
                    foreach (var p in props.Properties())
                        Assert.False(((JObject)p.Value).ContainsKey("__required"), $"{name}.{p.Name} leaks an internal key");
                }
                var names = tools.Select(t => (string)t["name"]).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
                foreach (var expected in new[] { "inspect_report", "batch_edit", "set_field_format", "export_report", "replace_picture", "add_table",
                                                 "add_group", "delete_group", "add_sort", "delete_sort", "add_running_total", "delete_running_total",
                                                 "set_parameter", "set_text_with_fields", "add_section", "delete_section", "move_object",
                                                 "set_page_setup", "verify_database" })
                    Assert.Contains(expected, names);
            }
        }

        [Fact]
        public void Unknown_method_returns_method_not_found()
        {
            using (var c = new McpClient())
                Assert.Equal(-32601, (int)c.Request("does/not/exist")["error"]["code"]);
        }

        [Fact]
        public void Unknown_tool_returns_error()
        {
            using (var c = new McpClient())
            {
                var res = c.Request("tools/call", new JObject { ["name"] = "nope", ["arguments"] = new JObject() });
                Assert.NotNull(res["error"]);
            }
        }

        [Fact]
        public void Malformed_json_is_reported_and_server_keeps_running()
        {
            using (var c = new McpClient())
            {
                c.SendRaw("{ this is not json");
                Assert.Equal(-32700, (int)c.ReadResponse()["error"]["code"]);
                Assert.NotNull(c.Request("ping")["result"]);
            }
        }

        [Fact]
        public void Notifications_get_no_response()
        {
            using (var c = new McpClient())
            {
                c.Notify("notifications/initialized");
                var pong = c.Request("ping"); // would read the notification's reply if one had been sent
                Assert.NotNull(pong["result"]);
            }
        }

        [Fact]
        public void Missing_and_non_rpt_files_are_rejected()
        {
            using (var c = new McpClient())
            {
                var missing = c.Call("inspect_report", new JObject { ["path"] = @"C:\definitely\missing.rpt" });
                Assert.True(missing.IsError);
                Assert.Contains("File not found", missing.Text);

                var txt = Path.GetTempFileName();
                try
                {
                    var wrong = c.Call("inspect_report", new JObject { ["path"] = txt });
                    Assert.True(wrong.IsError);
                    Assert.Contains("Not an .rpt file", wrong.Text);
                }
                finally { File.Delete(txt); }
            }
        }

        [Fact]
        public void List_reports_skips_backups_and_filters()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rptmcp-list-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "_rptmcp_backup"));
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "Invoice.rpt"), "");
            File.WriteAllText(Path.Combine(dir, "sub", "Label.rpt"), "");
            File.WriteAllText(Path.Combine(dir, "_rptmcp_backup", "Invoice_20260101_000000.rpt"), "");
            try
            {
                using (var c = new McpClient())
                {
                    var all = (JArray)c.CallOk("list_reports", new JObject { ["folder"] = dir })["reports"];
                    Assert.Equal(2, all.Count);
                    var top = (JArray)c.CallOk("list_reports", new JObject { ["folder"] = dir, ["recursive"] = false })["reports"];
                    Assert.Single(top);
                    var filtered = (JArray)c.CallOk("list_reports", new JObject { ["folder"] = dir, ["filter"] = "label" })["reports"];
                    Assert.EndsWith("Label.rpt", (string)filtered.Single()["path"]);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Add_table_validates_arguments_before_opening_the_file()
        {
            using (var c = new McpClient())
            {
                var noDb = c.Call("add_table", new JObject { ["path"] = @"C:\definitely\missing.rpt", ["table"] = "Orders", ["server"] = "srv" });
                Assert.True(noDb.IsError);
                Assert.Contains("server and database", noDb.Text);

                var badName = c.Call("add_table", new JObject { ["path"] = @"C:\definitely\missing.rpt", ["table"] = "a.b.c", ["server"] = "srv", ["database"] = "db" });
                Assert.True(badName.IsError);
                Assert.Contains("schema.Table", badName.Text);
            }
        }

        [Fact]
        public void New_structure_tools_are_allowed_in_batch_edit()
        {
            using (var c = new McpClient())
            {
                var batch = (JObject)((JArray)c.Request("tools/list")["result"]["tools"]).Single(t => (string)t["name"] == "batch_edit");
                foreach (var tool in new[] { "add_group", "add_sort", "add_running_total", "set_parameter", "set_text_with_fields", "add_section", "move_object", "set_page_setup" })
                    Assert.Contains(tool, (string)batch["description"]);
                Assert.DoesNotContain("verify_database", (string)batch["description"]);
            }
        }

        [Fact]
        public void Batch_edit_rejects_non_edit_tools_before_opening_the_file()
        {
            using (var c = new McpClient())
            {
                var res = c.Call("batch_edit", new JObject
                {
                    ["path"] = @"C:\definitely\missing.rpt",
                    ["operations"] = new JArray(new JObject { ["tool"] = "export_report" })
                });
                Assert.True(res.IsError);
                Assert.Contains("is not an edit tool", res.Text);
            }
        }
    }
}
