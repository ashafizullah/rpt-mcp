using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RptMcp
{
    internal sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    internal sealed class ToolRegistry
    {
        private sealed class Tool
        {
            public string Name, Description;
            public JObject Schema;
            public Func<JObject, JToken> Handler;
        }

        private readonly Dictionary<string, Tool> _tools = new Dictionary<string, Tool>();

        public int Count => _tools.Count;

        public ToolRegistry()
        {
            ReportTools.Register(this);
        }

        public void Add(string name, string description, JObject schema, Func<JObject, JToken> handler)
        {
            _tools[name] = new Tool { Name = name, Description = description, Schema = schema, Handler = handler };
        }

        public JArray List() => new JArray(_tools.Values.Select(t => new JObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = t.Schema
        }));

        /// <summary>Runs a tool handler directly (exceptions propagate). Used by batch_edit.</summary>
        public JToken Invoke(string name, JObject args) =>
            _tools.TryGetValue(name, out var tool) ? tool.Handler(args) : throw new ToolError("Unknown tool: " + name);

        public JObject Call(string name, JObject args)
        {
            if (name == null || !_tools.TryGetValue(name, out var tool))
                throw new RpcException(-32602, "Unknown tool: " + name);
            try
            {
                var result = tool.Handler(args);
                var text = result is JValue v && v.Type == JTokenType.String ? (string)v : result.ToString(Formatting.Indented);
                return new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) };
            }
            catch (Exception ex)
            {
                Program.Log($"{name} failed: {ex}");
                return new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = Describe(ex) }),
                    ["isError"] = true
                };
            }
        }

        internal static string Describe(Exception ex)
        {
            if (ex is ToolError) return ex.Message;
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e is COMException com ? $"{e.Message} (HRESULT 0x{com.ErrorCode:X8})" : e.Message;
                parts.Add($"{e.GetType().Name}: {msg}");
            }
            return string.Join("\n  -> ", parts);
        }

        // ---------- JSON schema helpers ----------

        public static JObject Schema(params JProperty[] props)
        {
            var required = new JArray();
            var properties = new JObject();
            foreach (var p in props)
            {
                var def = (JObject)p.Value;
                if (def.Remove("__required")) required.Add(p.Name);
                properties.Add(p.Name, def);
            }
            var s = new JObject { ["type"] = "object", ["properties"] = properties };
            if (required.Count > 0) s["required"] = required;
            return s;
        }

        public static JProperty Req(string name, string type, string desc) => Prop(name, type, desc, true);
        public static JProperty Opt(string name, string type, string desc) => Prop(name, type, desc, false);

        public static JProperty Enum(string name, string desc, bool required, params string[] values)
        {
            var p = Prop(name, "string", desc, required);
            ((JObject)p.Value)["enum"] = new JArray(values);
            return p;
        }

        private static JProperty Prop(string name, string type, string desc, bool required)
        {
            var def = new JObject { ["description"] = desc };
            if (type == "string[]") { def["type"] = "array"; def["items"] = new JObject { ["type"] = "string" }; }
            else if (type == "object[]") { def["type"] = "array"; def["items"] = new JObject { ["type"] = "object" }; }
            else def["type"] = type;
            if (required) def["__required"] = true;
            return new JProperty(name, def);
        }
    }
}
