using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RptMcp
{
    /// <summary>
    /// Minimal MCP (Model Context Protocol) server over stdio: newline-delimited JSON-RPC 2.0.
    /// Crystal Reports runtime 13 only runs on .NET Framework, so the protocol is implemented by hand.
    /// </summary>
    internal static class Program
    {
        private const string ServerName = "rpt-mcp";

        /// <summary>The &lt;Version&gt; from the project file (set per release from the git tag).</summary>
        private static readonly string ServerVersion =
            typeof(Program).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
        private static TextWriter _out;

        [STAThread]
        private static int Main(string[] args)
        {
            // Must run before any Crystal type is touched.
            AppDomain.CurrentDomain.AssemblyResolve += CrystalVersionFallback;

            var utf8 = new UTF8Encoding(false);
            _out = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
            // Anything else that writes to Console.Out must not corrupt the protocol stream.
            Console.SetOut(Console.Error);
            var input = new StreamReader(Console.OpenStandardInput(), utf8);

            var tools = new ToolRegistry();
            Log($"{ServerName} started, {tools.Count} tools");

            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject msg;
                try { msg = JObject.Parse(line); }
                catch (Exception ex)
                {
                    Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = Error(-32700, "Parse error: " + ex.Message) });
                    continue;
                }

                var id = msg["id"];
                var method = (string)msg["method"];
                if (method == null) continue; // a response to something we never send
                bool isNotification = id == null;

                try
                {
                    JToken result = Handle(method, msg["params"] as JObject ?? new JObject(), tools);
                    if (!isNotification)
                        Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JObject() });
                }
                catch (RpcException ex)
                {
                    if (!isNotification)
                        Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Error(ex.Code, ex.Message) });
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                    if (!isNotification)
                        Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Error(-32603, ex.Message) });
                }
            }
            return 0;
        }

        private static JToken Handle(string method, JObject p, ToolRegistry tools)
        {
            switch (method)
            {
                case "initialize":
                    return new JObject
                    {
                        ["protocolVersion"] = (string)p["protocolVersion"] ?? "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
                        ["instructions"] =
                            "Edit SAP Crystal Reports (.rpt) files through the Crystal Reports runtime. " +
                            "Always call inspect_report first to learn object, section, formula and table names. " +
                            "Edits overwrite the file in place (a timestamped backup goes to _rptmcp_backup next to it) unless output_path is given. " +
                            "Positions and sizes are in twips (1440 = 1 inch, 567 ≈ 1 cm). " +
                            "To visually verify a change, export_report to png and open the image."
                    };
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return new JObject();
                case "tools/list":
                    return new JObject { ["tools"] = tools.List() };
                case "tools/call":
                    return tools.Call((string)p["name"], p["arguments"] as JObject ?? new JObject());
                default:
                    throw new RpcException(-32601, "Method not found: " + method);
            }
        }

        private static JObject Error(int code, string message) => new JObject { ["code"] = code, ["message"] = message };

        private static void Send(JObject msg)
        {
            lock (_out) _out.WriteLine(msg.ToString(Formatting.None));
        }

        /// <summary>
        /// Crystal runtime SPs install their assemblies either as 13.0.2000.0 (older) or 13.0.4000.0 (SP 21+),
        /// without a publisher policy between them. When the version this exe was built against is missing,
        /// load the other one so a single build runs on either runtime.
        /// </summary>
        private static readonly Version[] CrystalVersions = { new Version(13, 0, 4000, 0), new Version(13, 0, 2000, 0) };
        [ThreadStatic] private static bool _resolving;

        private static System.Reflection.Assembly CrystalVersionFallback(object sender, ResolveEventArgs e)
        {
            var wanted = new System.Reflection.AssemblyName(e.Name);
            if (_resolving || !wanted.Name.StartsWith("CrystalDecisions.", StringComparison.Ordinal) || wanted.Version == null) return null;
            _resolving = true;
            try
            {
                foreach (var v in CrystalVersions)
                {
                    if (v == wanted.Version) continue;
                    var alt = new System.Reflection.AssemblyName(e.Name) { Version = v };
                    try { return System.Reflection.Assembly.Load(alt); } catch (FileNotFoundException) { } catch (FileLoadException) { }
                }
                return null;
            }
            finally { _resolving = false; }
        }

        internal static void Log(string text) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
    }

    internal sealed class RpcException : Exception
    {
        public int Code { get; }
        public RpcException(int code, string message) : base(message) { Code = code; }
    }
}
