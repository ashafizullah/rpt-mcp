# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **`add_group` / `delete_group`**: group a report on a field (with date grouping per day/week/month/… and ascending/descending order). Crystal adds the group header and footer sections, named after the field (e.g. `ShiftHeaderSection1`); the tool returns them. `delete_group` refuses while those sections hold objects or formulas/running totals summarize per the group, unless `force`.
- **`add_sort` / `delete_sort`**: record sorts. `add_sort` on a field that is already sorted (including a group's field) changes the direction.
- **`add_running_total` / `delete_running_total`**: running totals with conditional evaluation (each record, on change of a field or group, on a formula) and reset (never, on change of a field or group, on a formula).
- **`set_parameter`**: change a parameter's prompt, type, multiple values or default values in place, without deleting it (which would break the formulas using it).
- **`set_text_with_fields`**: text objects with embedded fields, e.g. `Shift : {T.Shift}` or `Page {PageNumber} of {TotalPageCount}`.
- **`add_section` / `delete_section`** and **`move_object`** (moves an object to another section keeping its name and formatting).
- **`set_page_setup`**: paper size, orientation and margins. Set through a user paper size, because the runtime ignores the engine's `PaperSize`/`PaperOrientation` when rendering without a printer.
- **`verify_database`**: checks the report against its database and reports missing or retyped columns, what uses them and logon/provider problems, without saving. (Crystal's own Verify Database silently deletes objects bound to a missing column.)
- `add_field_object` and embedded fields accept **special fields** (`RecordNumber`, `PageNumber`, `TotalPageCount`, `PageNofM`, `PrintDate`, `GroupNumber`, `FileName`, …); `inspect_report` marks them `"special": true`.
- `inspect_report` shows the printable page size (`content_twips`).

### Fixed
- `add_field_object` with a running total (`{#Name}`) produced a report that could not be saved. The field is now shown through a formula `{@Name}` = `{#Name}`.
- Usage guards (`delete_formula`, `delete_parameter`, `remove_table`) also count running totals that summarize, evaluate or reset on the field.

## [0.1.3] - 2026-10-01

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

[Unreleased]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/ashafizullah/rpt-mcp/releases/tag/v0.1.0
