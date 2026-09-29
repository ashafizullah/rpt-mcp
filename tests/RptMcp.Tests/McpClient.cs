using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RptMcp.Tests
{
    /// <summary>Starts rpt-mcp.exe and exchanges newline-delimited JSON-RPC messages with it.</summary>
    internal sealed class McpClient : IDisposable
    {
        private readonly Process _process;
        private int _nextId = 1;

        public McpClient()
        {
            _process = Process.Start(new ProcessStartInfo(ServerPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true,
            });
            _process.ErrorDataReceived += (s, e) => { }; // drain logs
            _process.BeginErrorReadLine();
            Initialize = Request("initialize", new JObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject() });
            Notify("notifications/initialized");
        }

        public JObject Initialize { get; }

        /// <summary>src/bin/&lt;Configuration&gt;/rpt-mcp.exe, found by walking up from the test output folder.</summary>
        public static string ServerPath
        {
            get
            {
                var config = AppDomain.CurrentDomain.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug") ? "Debug" : "Release";
                for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    var exe = Path.Combine(dir.FullName, "src", "bin", config, "rpt-mcp.exe");
                    if (File.Exists(exe)) return exe;
                }
                throw new FileNotFoundException("rpt-mcp.exe not found; build src/RptMcp.csproj first.");
            }
        }

        public JObject Request(string method, JObject parameters = null)
        {
            var id = _nextId++;
            SendRaw(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() }.ToString(Formatting.None));
            return ReadResponse(id);
        }

        public void Notify(string method) =>
            SendRaw(new JObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToString(Formatting.None));

        public void SendRaw(string line)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }

        public JObject ReadResponse(int? id = null)
        {
            var line = _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("Server closed stdout.");
            var msg = JObject.Parse(line);
            if (id != null && (int?)msg["id"] != id) throw new InvalidOperationException($"Expected response {id}, got: {line}");
            return msg;
        }

        /// <summary>Calls a tool; returns (isError, text).</summary>
        public (bool IsError, string Text) Call(string tool, JObject args)
        {
            var res = Request("tools/call", new JObject { ["name"] = tool, ["arguments"] = args });
            if (res["error"] != null) throw new InvalidOperationException("RPC error: " + res["error"]);
            var result = (JObject)res["result"];
            return ((bool?)result["isError"] ?? false, (string)result["content"][0]["text"]);
        }

        public JObject CallOk(string tool, JObject args)
        {
            var (isError, text) = Call(tool, args);
            if (isError) throw new InvalidOperationException($"{tool} failed: {text}");
            return JObject.Parse(text);
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5000)) _process.Kill();
            }
            catch { /* ignore */ }
            _process.Dispose();
        }
    }
}
