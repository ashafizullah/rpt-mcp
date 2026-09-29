# rpt-mcp

**An MCP server for SAP Crystal Reports `.rpt` files.**

An [MCP](https://modelcontextprotocol.io) server that lets AI assistants (Claude Code, Claude Desktop, Cursor, …) **read and edit SAP Crystal Reports `.rpt` files**.

`.rpt` is a binary format, so an AI cannot edit it as text. This server wraps the official Crystal Reports .NET runtime (Engine + in-process Report Application Server) and exposes safe, structured edit operations as MCP tools.

## Tools

| Tool | What it does |
|---|---|
| `list_reports` | List `.rpt` files in a folder |
| `inspect_report` | Page setup, tables / SQL commands / connections, links, parameters, formulas, selection formulas, groups, sorts, sections and every object (position, text, bound field, font), including **conditional formulas** hidden in formatting (suppress, color, number format…) |
| `batch_edit` | Run several edit operations with one load, one backup and one save; all-or-nothing |
| `diff_reports` | Structural diff between two reports (e.g. backup vs. edited) |
| `set_formula` / `delete_formula` | Edit, create or delete formula fields |
| `set_selection_formula` | Record or group selection formula |
| `add_parameter` / `delete_parameter` | Report parameters |
| `set_command_sql` | Replace the SQL of an existing SQL Command table (experimental) |
| `set_datasource` | Repoint tables to another server/database, optionally switching OLE DB provider (persisted) |
| `remove_table` | Remove a table / command (guarded: refuses while its fields are used unless `force`) |
| `set_text` | Change a text object's text |
| `set_object_props` | Position/size, font, color, alignment, suppress, can-grow; for lines/boxes also end point, line style/thickness/color and box fill |
| `add_line` / `add_box` | Horizontal/vertical lines and boxes (fill, rounded corners), optionally spanning into a later section |
| `add_picture` / `replace_picture` | Insert an image, or swap a picture (e.g. a logo) keeping its name, position and size |
| `set_field_format` | Number/date/time/boolean format of a field: decimals, thousands & decimal symbols, negatives, currency, zero text, date order/separator, 12/24h… or a custom pattern (`#,##0.00`, `yyyy-MM-dd HH:mm`) |
| `set_condition_formula` | Set/clear conditional formulas (suppress, font color, display string, border, number format…) |
| `set_section_props` | Section height, suppress, page breaks, keep-together, background |
| `add_text_object` / `add_field_object` / `delete_object` | Add or remove report objects |
| `export_report` | Run the report and export to PDF/Excel/Word/CSV/… (useful to visually verify edits). Rows can be passed inline (`data`) for DataSet/XML-based reports or quick previews without a database |

Positions and sizes are in **twips** (1440 = 1 inch, 567 ≈ 1 cm), Crystal's native unit.

All tools accept `subreport` to work inside a subreport.

**Safety**
- Every edit overwrites the file in place only after copying the original to `_rptmcp_backup/<name>_<timestamp>.rpt` next to it. Pass `output_path` to write to a new file instead.
- `delete_formula`, `delete_parameter` and `remove_table` refuse while the field is still used (field objects, fields embedded in text objects, formulas, selection/conditional formulas, groups, sorts, SQL commands) and list where it is used. `force: true` deletes the bound objects too. Without this guard, Crystal silently drops every object bound to a deleted formula.

## Requirements

- Windows
- .NET Framework 4.8
- **SAP Crystal Reports runtime for .NET Framework (v13, 64-bit)**, or *SAP Crystal Reports for Visual Studio* (SP 20+ recommended). Download it free from SAP. The runtime is proprietary SAP software and is **not** included in this repository.
- To build from source: .NET SDK 6+ (it builds `net48`).

## Build

```powershell
cd src
dotnet build -c Release
# -> src\bin\Release\rpt-mcp.exe
```

The project references the Crystal assemblies from the GAC (`C:\Windows\assembly\GAC_MSIL`). If yours are elsewhere, override with `dotnet build -p:CrGacMsil=<folder>`.

For a 32-bit Crystal runtime, build with `-p:PlatformTarget=x86`.

## Register with an MCP client

Claude Code:

```powershell
claude mcp add rpt-mcp -- "C:\path\to\rpt-mcp.exe"
```

Claude Desktop / other clients (`mcpServers` config):

```json
{
  "mcpServers": {
    "rpt-mcp": { "command": "C:\\path\\to\\rpt-mcp.exe" }
  }
}
```

## Database connections (optional)

Editing does not need a database. `export_report`, `set_command_sql` and `set_datasource` usually do.
Pass `server` / `database` / `user` / `password` / `integrated` directly, or create `connections.json` next to the exe (or point `RPTMCP_CONNECTIONS` at it) and pass `connection: "<name>"`:

```json
{
  "dev": { "server": "localhost\\SQLEXPRESS", "database": "Northwind", "integrated": true }
}
```

`connections.json` is git-ignored; never commit credentials.

## Example prompts

- "Inspect `C:\reports\Invoice.rpt` and change the title to *Tax Invoice*, bold 14pt."
- "Add a formula `{@FullName}` = first + last name and put it in the detail section next to the customer code."
- "Point every table in all reports under `C:\reports` to server `SQL02`, database `SalesProd`."
- "Export Invoice.rpt to PDF with `OrderNo = 1001` so I can check the layout."

## Tips

- Old reports often use the legacy `SQLOLEDB` provider, which cannot connect to LocalDB or TLS 1.2-only servers. Use `set_datasource` with `provider: "MSOLEDBSQL"` (Microsoft OLE DB Driver for SQL Server) to fix that.

## Limitations

- **Creating a new SQL Command is not supported.** The in-process Crystal runtime rejects it with "Failed to load database information", even though adding a plain table on the same connection works. `set_command_sql` can only edit a command that already exists, and it has not yet been verified against a report containing one.
- Charts, cross-tabs and OLAP grids can be inspected and moved, but not structurally edited.
- Line styles: the runtime rejects `double`, and draws `dashed`/`dotted` lines as hairlines. Pictures are stored as bitmaps (transparency is flattened to white).
- `set_command_sql` and `set_datasource` go through Crystal's table-location API, which connects to the database to validate the schema.
- Tested with Crystal Reports runtime 13.0.2000 (x64) on Windows 11 and SQL Server LocalDB.

## Protocol

JSON-RPC 2.0 over stdio (newline-delimited). This is implemented by hand because the Crystal runtime only runs on .NET Framework. Logs go to stderr.

## License and legal

- rpt-mcp is released under the [MIT License](LICENSE).
- It is an independent project, **not affiliated with, endorsed or sponsored by SAP SE**. SAP and Crystal Reports are trademarks or registered trademarks of SAP SE in Germany and other countries. They are used here only to describe compatibility.
- The SAP Crystal Reports runtime is **not** distributed with this project. Each user installs it from SAP and is bound by SAP's license terms. SAP describes the runtime as free for client (desktop) applications. rpt-mcp runs as a local, single-user process, which is that kind of deployment. If you run it as a shared server that several users reach, check SAP's licensing for server applications (e.g. the Crystal Reports Developer Advantage license).
- Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
