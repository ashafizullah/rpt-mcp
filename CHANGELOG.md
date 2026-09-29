# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/ashafizullah/rpt-mcp/releases/tag/v0.1.0
