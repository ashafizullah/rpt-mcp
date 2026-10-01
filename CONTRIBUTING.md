# Contributing

Thanks for helping! Issues and pull requests are welcome.

## Setup

1. Windows with .NET Framework 4.8 and a .NET SDK (6+).
2. Install the SAP Crystal Reports runtime for .NET Framework (x64) from SAP.
3. Build and test:

```powershell
dotnet build RptMcp.sln -c Release
dotnet test RptMcp.sln -c Release
```

## Tests

- `ProtocolTests` start `rpt-mcp.exe` and talk MCP over stdio. They need the runtime but no report.
- `ReportTests` edit a **copy** of a real report. They are skipped unless `RPTMCP_TEST_REPORT` points to a `.rpt` that has at least one text object:

```powershell
$env:RPTMCP_TEST_REPORT = "C:\reports\Any.rpt"
dotnet test RptMcp.sln -c Release
```

- The `add_table` test also needs a reachable SQL Server table, read with Windows integrated security:

```powershell
$env:RPTMCP_TEST_DB_SERVER   = "(localdb)\MSSQLLocalDB"
$env:RPTMCP_TEST_DB_DATABASE = "rptmcp"
$env:RPTMCP_TEST_DB_TABLE    = "dbo.ProductionReport"
```

## Guidelines

- Keep tools safe: edits go through `Edit`/`ReportIO.Save` (automatic backup), and destructive operations should refuse when something still depends on them.
- Prefer returning a `ToolError` with a helpful message (valid values, what to call next) over a raw exception.
- **Never commit `.rpt` files that contain confidential data**, or SAP binaries. The repository does not redistribute the Crystal runtime.
- Add a line to `CHANGELOG.md` under *Unreleased*.
