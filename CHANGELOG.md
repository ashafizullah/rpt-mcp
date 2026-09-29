# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Automated tests: protocol and validation tests (always run), plus report tests against any `.rpt` given in `RPTMCP_TEST_REPORT`.
- CI and tag-triggered release workflows.

### Changed
- `batch_edit` validates every operation's tool name before opening the report.
- The build picks up either 13.0.2000.0 or 13.0.4000.0 Crystal assemblies automatically.

## [0.1.0] - 2026-09-29

First public release: inspect, diff and edit `.rpt` files (text, fonts, formulas, parameters, data sources, field formats, conditional formulas, lines, boxes, pictures), with backups, usage guards, `batch_edit` and `export_report`.

[Unreleased]: https://github.com/ashafizullah/rpt-mcp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ashafizullah/rpt-mcp/releases/tag/v0.1.0
