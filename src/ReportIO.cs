using System;
using System.IO;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.Shared;
using Newtonsoft.Json.Linq;

namespace RptMcp
{
    /// <summary>Loading, saving (with automatic backup) and DB logon for .rpt files.</summary>
    internal static class ReportIO
    {
        public const string BackupFolder = "_rptmcp_backup";

        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolError("path is required");
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!File.Exists(full)) throw new ToolError("File not found: " + full);
            if (!full.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) throw new ToolError("Not an .rpt file: " + full);
            return full;
        }

        /// <summary>Opens a report (by temp copy, so the original can be overwritten) and always closes it.</summary>
        public static T With<T>(string path, Func<ReportDocument, T> action)
        {
            var rd = new ReportDocument();
            try
            {
                rd.Load(ResolvePath(path), OpenReportMethod.OpenReportByTempCopy);
                return action(rd);
            }
            finally
            {
                try { rd.Close(); } catch { /* ignore */ }
                rd.Dispose();
            }
        }

        /// <summary>
        /// Saves the report. Without output_path the source file is overwritten after it is copied to
        /// &lt;dir&gt;\_rptmcp_backup\&lt;name&gt;_yyyyMMdd_HHmmss.rpt.
        /// </summary>
        public static JObject Save(ReportDocument rd, JObject args)
        {
            var source = ResolvePath((string)args["path"]);
            var output = (string)args["output_path"];
            var target = string.IsNullOrWhiteSpace(output) ? source : Path.GetFullPath(Environment.ExpandEnvironmentVariables(output.Trim().Trim('"')));
            if (!target.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) throw new ToolError("output_path must end with .rpt");

            string backup = null;
            if (File.Exists(target))
            {
                var dir = Path.Combine(Path.GetDirectoryName(target), BackupFolder);
                Directory.CreateDirectory(dir);
                backup = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(target)}_{DateTime.Now:yyyyMMdd_HHmmss}.rpt");
                File.Copy(target, backup, true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
            }

            rd.SaveAs(target);
            var res = new JObject { ["saved"] = target };
            if (backup != null) res["backup"] = backup;
            return res;
        }

        public static ReportDocument Scope(ReportDocument rd, string subreport) =>
            string.IsNullOrWhiteSpace(subreport) ? rd : rd.OpenSubreport(subreport);

        // ---------- database logon ----------

        public sealed class Logon
        {
            public string Server, Database, User, Password;
            public bool Integrated;
            /// <summary>True when integrated was given explicitly (arg or connection), so it may be written to the report.</summary>
            public bool IntegratedSpecified;
            public bool HasCredentials => Integrated || !string.IsNullOrEmpty(User);
        }

        /// <summary>
        /// Reads logon from args: an optional named "connection" (from connections.json next to the exe,
        /// or the file in %RPTMCP_CONNECTIONS%) overridden by explicit server/database/user/password/integrated.
        /// </summary>
        public static Logon GetLogon(JObject args)
        {
            var l = new Logon();
            var name = (string)args["connection"];
            if (!string.IsNullOrWhiteSpace(name))
            {
                var c = LoadConnections()[name] as JObject ?? throw new ToolError($"Connection '{name}' not found in {ConnectionsPath}");
                l.Server = (string)c["server"];
                l.Database = (string)c["database"];
                l.User = (string)c["user"];
                l.Password = (string)c["password"];
                l.Integrated = (bool?)c["integrated"] ?? false;
                l.IntegratedSpecified = c["integrated"] != null;
            }
            l.Server = (string)args["server"] ?? l.Server;
            l.Database = (string)args["database"] ?? l.Database;
            l.User = (string)args["user"] ?? l.User;
            l.Password = (string)args["password"] ?? l.Password;
            l.Integrated = (bool?)args["integrated"] ?? l.Integrated;
            l.IntegratedSpecified |= args["integrated"] != null;
            return l;
        }

        public static string ConnectionsPath =>
            Environment.GetEnvironmentVariable("RPTMCP_CONNECTIONS") is string p && p.Length > 0
                ? p
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connections.json");

        public static JObject LoadConnections() =>
            File.Exists(ConnectionsPath) ? JObject.Parse(File.ReadAllText(ConnectionsPath)) : new JObject();

        /// <summary>Applies logon to every table of the report and its subreports (runtime only, not persisted).</summary>
        public static void ApplyLogon(ReportDocument rd, Logon l)
        {
            ApplyLogonToTables(rd, l);
            foreach (ReportDocument sub in rd.Subreports) ApplyLogonToTables(sub, l);
        }

        private static void ApplyLogonToTables(ReportDocument rd, Logon l)
        {
            foreach (Table t in rd.Database.Tables)
            {
                var li = t.LogOnInfo;
                var ci = li.ConnectionInfo;
                if (!string.IsNullOrEmpty(l.Server)) ci.ServerName = l.Server;
                if (!string.IsNullOrEmpty(l.Database)) ci.DatabaseName = l.Database;
                ci.IntegratedSecurity = l.Integrated;
                if (!l.Integrated)
                {
                    ci.UserID = l.User ?? ci.UserID;
                    ci.Password = l.Password ?? "";
                }
                t.ApplyLogOnInfo(li);
            }
        }
    }
}
