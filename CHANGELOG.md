# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **`add_table` with `copy_connection_from`**: clones the connection of an existing table verbatim, keeping its driver. Needed for databases Crystal can only reach through the driver already stored in the report — e.g. SAP Business One on HANA, which uses the ODBC (RDO) driver and has no OLE DB provider. The connection string is rewritten to the target server/database. The table may also be taken from the main report when adding to a subreport.
- **`set_datasource` with `connection_string`**: replaces the whole ODBC connection string. The ODBC/RDO driver keeps it in a single property, so the report kept using the old server (e.g. the SQL Server of the previous ERP) even after the server and database were changed.
- **`set_datasource` with `rename_to`**: points a table of the previous ERP at its equivalent in the new one (`{OldTable.X}` becomes `{NewTable.X}`).
- **`set_table_link`**: change how two tables are joined (`inner` / `left`) and on which fields. The link and its operator live in two different collections (`TableJoins` keeps the operator, `TableLinks` the fields) and the designer derives one from the other, so both are written.
- **`set_table_link` with `join="none"`**: drop the link between two tables.
- **`remove_table` with `orphaned=true`**: remove a table of a database that no longer exists (e.g. the tables of the previous ERP). Crystal cannot even resolve it - it tries to connect to read its columns - so it is dropped without connecting.
- **`add_table` with `sql=`**: add a SQL command table - the report uses the result of the query as its data source. The way to read a table whose columns are reserved words in the database (Crystal writes the SQL without quoting them, and the query fails with "invalid column name"), or to filter in the database instead of in the report.
- **`set_command_sql`**: replace the SQL text of a SQL command table.
- **`replace_table_with_sql`**: replace an existing table by a SQL command table with the *same alias*, so every `{Table.Field}` reference in the report keeps working. This is the way to migrate a view of the previous ERP to a query over the new database without touching the formulas, the objects, the groups and the running totals: the query returns the columns the report uses, aliased to the legacy names. The table keeps its links; its field list becomes the columns of the query.
- **`remove_table_field`**: remove a column from the model of a table, so it stops being part of the query (same "reserved word" problem as above).
- **`set_table_link` with `drop_conflicts=true`**: a table that can be reached by two different paths (e.g. `CRD1` through `ODLN` and also through `OCRD`) cannot be joined with an outer join - Crystal builds an invalid query and the report fails with "Error de conector de base de datos". The links that reach the same table are now reported in `conflicting_links`, and dropped on request.

### Changed
- **`set_text`** now says in its description that the fields embedded in the object are removed together with its text (`set_text_with_fields` keeps them). The behaviour is the one of the runtime, but it surprised everyone.

### Fixed
- **`inspect_report` / `diff_reports` failed with "Can not add property {X}"** when a text object embeds the same field more than once (e.g. `{CRD1.City}` twice). The embedded-field map is now de-duplicated by data source, so `include_objects=true` works on such reports.
- **`export_report` could not set a subreport parameter**: it was only looked up in the main report's parameter list, but a subreport's parameters live in the subreport. The value is now also set on the subreport's own document, and the parameters that still have no value are listed instead of the bare "missing parameter values" from the runtime.
- **`set_subreport_links` refused a numeric link** between an int32 column and a "number" parameter. The numeric field kinds are now interchangeable.
- **The build targeted the 32-bit Crystal runtime by default.** The assembly search now prefers the .NET 4 GAC (`v4.0_13.0.4000.0`, 64-bit) and only falls back to the legacy GAC (`13.0.2000.0`) on older SPs, matching the release build.
- **`set_table_link` only changed the operator of an existing link, never its fields** (the fields live in the join's `FieldLinks`). The fields are now rewritten as well.
- **`set_table_link` dropped the other links of the report**: rebuilding the collection re-used the link objects, which already belonged to it. Each link is now cloned before the collection is assigned.
- **`remove_table` with `force` wiped the whole subreport** (its formulas and objects): the fallback emptied the fields of the table, and that rewrote the data definition. The links that point at the table are now dropped instead, and only then the table is removed.
- **A link whose fields no longer exist could not be removed**: Crystal keeps the link in the table's own properties. Recreating the table (`remove_table` + `add_table`) is the way to get rid of it; the formulas and objects that used it have to be recreated as well.
- **`set_subreport_links` wrote the filter of a field link with the parameter name it expected**, but Crystal may rename the parameter it creates behind the link (`Pm-Orders.Customer??01`) when the name is taken. The filter now uses the name Crystal really used, so the subreport is not filtered by a parameter that is never filled.
- **`set_table_link` changed nothing**: the link does not live in the `TableLinks` collection alone, the designer keeps it in the table, so rebuilding the collection was discarded on save. The link is now changed through the controller (`RemoveTableLink` / `AddTableLink`), which persists.
- **`set_datasource` repointed the connection but not the tables**: the qualified name (`OldDb.ODLN`) is a property of the table, so the query kept using the old database. It is now rewritten together with the connection.
- **`add_subreport` with `from_subreport`**: insert a subreport that is already embedded in the report again (same data definition, same links). The runtime refuses to add a subreport object to a report, so this only works for a report that Crystal imports from a file - the error says so.

## [0.1.6] - 2026-10-01

### Added
- **`export_report` to `png` / `jpg`**: renders the report pages as images (the Crystal runtime cannot export images, so it exports a temporary PDF and renders it with the PDF renderer built into Windows). `dpi` (default 150) and `pages` (`"1"`, `"1-3"`, `"1,3"`) choose what to render; one page goes to `output_path`, several to `<name>-<page>.png`. The result lists each page's pixel size. Lets an agent check a layout change itself.
- **`set_condition_formula` on sections**: pass `section` instead of `object`, e.g. `Format.EnableSuppress` to hide a page header once the table has ended. Crystal ignores conditional suppress on lines and boxes, so suppressing their section is the way to hide them.

### Fixed
- `set_formula` without `text` (e.g. a misnamed argument) silently created or blanked the formula; it is now an error.

## [0.1.5] - 2026-10-01

### Added
- **`add_subreport`**: insert another `.rpt` as a subreport into a section (the section grows to fit), optionally with links.
- **`set_subreport_links`**: link a subreport to the main report, parameter to parameter or field to field. For a field link the subreport's record selection is extended with `{field} = {?Pm-...}` (the runtime does not add it, unlike the designer); removing the link removes that filter. `inspect_report` lists the links (`subreport_links`).

### Fixed
- `add_parameter` (and `set_parameter`) inside a subreport failed with "A subreport name is required".
- `move_object` on a subreport object now explains that Crystal cannot move subreports, instead of failing midway.

## [0.1.4] - 2026-10-01

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

[Unreleased]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.6...HEAD
[0.1.6]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/ashafizullah/rpt-mcp/releases/tag/v0.1.0
