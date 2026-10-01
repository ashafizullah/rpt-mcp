# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **`add_table`**: add a database table (OLE DB / ADO, default provider `MSOLEDBSQL`) to a report, including one built from scratch with no data source yet. Crystal reads the column list from the server; the tool returns it so the columns can be placed with `add_field_object`. Also allowed inside `batch_edit`.

### Fixed
- `set_object_props` (and anything else reading a field object's font or color) failed with `NullReferenceException` on a field object added earlier in the same `batch_edit`. New field objects now get Crystal's default font (Arial 10pt, black) explicitly.
- `set_datasource` on a report without tables silently did nothing; it now reports that and points to `add_table`.
- `set_datasource` with `integrated` keeps the stored type of `Integrated Security` (a Boolean in reports saved by the Crystal designer) instead of always writing a string.

## [0.1.2] - 2026-09-29

### Added
- Each release now also ships an **MCP Bundle** (`rpt-mcp-<version>.mcpb`) and is published to the official MCP Registry as `io.github.ashafizullah/rpt-mcp`.

### Fixed
- `initialize` reported the server version as 0.1.0 regardless of the release; it now reports the build's version.

### Changed
- Release binaries no longer embed local build paths (deterministic build with `PathMap`).

## [0.1.1] - 2026-09-29

### Fixed
- **Works with newer Crystal Reports runtimes (SP21+, e.g. SP36).** They install the assemblies as 13.0.4000.0 in the .NET 4 GAC (the report-modification assemblies 64-bit only), while older SPs use 13.0.2000.0 in the legacy GAC, with no publisher policy between them. rpt-mcp now loads whichever version is installed, so one build runs on both.
- The build finds the Crystal assemblies in the legacy or the .NET 4 GAC (`GAC_MSIL` or `GAC_64`), and fails with a clear message when the runtime is missing.

### Added
- Automated tests: protocol and validation tests (always run), plus report tests against any `.rpt` given in `RPTMCP_TEST_REPORT`.
- CI (build + tests on the SAP runtime) and a tag-triggered release workflow.
- README badges, CONTRIBUTING, bug report template, Dependabot.

### Changed
- `batch_edit` validates every operation's tool name before opening the report.
- Newtonsoft.Json 13.0.4.

## [0.1.0] - 2026-09-29

First public release: inspect, diff and edit `.rpt` files (text, fonts, formulas, parameters, data sources, field formats, conditional formulas, lines, boxes, pictures), with backups, usage guards, `batch_edit` and `export_report`.

[Unreleased]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/ashafizullah/rpt-mcp/releases/tag/v0.1.0
