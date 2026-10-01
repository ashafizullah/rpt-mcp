# rpt-mcp

[English](README.md) | **中文**

[![CI](https://github.com/ashafizullah/rpt-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/ashafizullah/rpt-mcp/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/ashafizullah/rpt-mcp)](https://github.com/ashafizullah/rpt-mcp/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![MCP](https://img.shields.io/badge/MCP-server-blue)](https://modelcontextprotocol.io)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20.NET%20Framework%204.8-lightgrey)

**一个用于 SAP Crystal Reports `.rpt` 文件的 MCP 服务器。**

这是一个 [MCP](https://modelcontextprotocol.io) 服务器，让 AI 助手（Claude Code、Claude Desktop、Cursor 等）能够**读取和编辑 SAP Crystal Reports `.rpt` 文件**。

`.rpt` 是二进制格式，AI 无法以文本方式编辑它。本服务器封装了官方的 Crystal Reports .NET 运行时（Engine + 进程内 Report Application Server），并以 MCP 工具的形式对外暴露安全、结构化的编辑操作。

## 工具

| 工具 | 功能 |
|---|---|
| `list_reports` | 列出某个文件夹中的 `.rpt` 文件 |
| `inspect_report` | 页面设置，表 / SQL 命令 / 连接、链接、参数、公式、选择公式、分组、排序、节以及每一个对象（位置、文本、绑定字段、字体），包括隐藏在格式设置中的**条件公式**（抑制显示、颜色、数字格式……） |
| `batch_edit` | 在一次加载、一次备份和一次保存中运行多个编辑操作；全部成功或全部回滚 |
| `diff_reports` | 两个报表之间的结构差异对比（例如备份与已编辑版本） |
| `set_formula` / `delete_formula` | 编辑、创建或删除公式字段 |
| `set_selection_formula` | 记录选择公式或组选择公式 |
| `add_parameter` / `set_parameter` / `delete_parameter` | 报表参数；`set_parameter` 可原地修改提示文本、类型、多值或默认值，并保留所有引用它的公式 |
| `add_group` / `delete_group` | 按字段分组（按班次、按天、按月……）；返回新建的组页眉/组页脚节。小计是组页脚中的公式，例如 `Sum({T.Qty}, {T.Shift})` |
| `add_sort` / `delete_sort` | 记录排序（升序/降序）；对已分组的字段使用 `add_sort` 会翻转该分组的排序方向 |
| `add_running_total` / `delete_running_total` | 运行总计（求和、计数、非重复计数、平均值、最小值、最大值），可按每条记录、按字段/分组变化或按公式求值，重置方式可为从不 / 按字段 / 按分组 / 按公式 |
| `set_command_sql` | 替换已有 SQL 命令表的 SQL（实验性） |
| `add_table` | 添加数据库表（OLE DB / ADO，例如 SQL Server 或 LocalDB），以便将其列放到报表上；返回列清单 |
| `set_datasource` | 将表重新指向另一个服务器/数据库，可选切换 OLE DB 提供程序（会持久化保存） |
| `remove_table` | 移除表 / 命令（有防护：只要其字段仍被使用就会拒绝，除非指定 `force`） |
| `set_text` | 修改文本对象的文本 |
| `set_text_with_fields` | 带内嵌字段的文本，例如 `Shift : {T.Shift}` 或 `Page {PageNumber} of {TotalPageCount}`，写入新建或已有的文本对象 |
| `set_object_props` | 位置/大小、字体、颜色、对齐、抑制显示、可增大；对于线条/框还支持终点、线型/线粗/线颜色以及框的填充 |
| `add_line` / `add_box` | 水平/垂直线条与框（填充、圆角），可选择延伸至后续节 |
| `add_picture` / `replace_picture` | 插入图片，或替换图片（例如 logo），并保留其名称、位置和大小 |
| `set_field_format` | 字段的数字/日期/时间/布尔格式：小数位、千位与小数分隔符、负数、货币、零值文本、日期顺序/分隔符、12/24 小时……或自定义模式（`#,##0.00`、`yyyy-MM-dd HH:mm`） |
| `set_condition_formula` | 设置/清除条件公式（抑制显示、字体颜色、显示字符串、边框、数字格式……） |
| `set_section_props` | 节高、抑制显示、分页符、保持在一起、背景 |
| `add_section` / `delete_section` | 添加节（例如第二个详细资料节）或删除节（节内仍有对象时会拒绝） |
| `move_object` | 将对象移动到另一个节，并保留其名称、字体、格式和条件公式 |
| `add_subreport` / `set_subreport_links` | 将另一个 `.rpt` 作为子报表插入并建立链接：主报表参数 → 子报表参数，或主报表字段 → 子报表字段（用于筛选子报表，例如按分组） |
| `set_page_setup` | 纸张大小（A4、Letter、Legal……或自定义）、方向和页边距；返回可打印宽度 |
| `add_text_object` / `add_field_object` / `delete_object` | 添加或移除报表对象；字段对象还可以显示特殊字段（`RecordNumber`、`PageNumber`、`TotalPageCount`、`PageNofM`、`PrintDate`、`GroupNumber`、`FileName`……） |
| `verify_database` | 根据数据库校验报表：已不存在的列（以及哪些对象在使用它们）、类型变更、登录/提供程序问题。绝不保存 |
| `export_report` | 运行报表并导出为 PDF/Excel/Word/CSV/…… 或 PNG/JPG 图片（每页一张，可选 `dpi` 和 `pages`），以便 agent 亲自查看版面。图片导出通过 Windows 10 / Server 2016+ 内置的 PDF 渲染器进行；相比 JPG，PNG 文字更清晰、体积更小。对于基于 DataSet/XML 的报表，或无需数据库的快速预览，行数据可通过 `data` 内联传入 |

位置和大小以 **twips** 为单位（1440 = 1 英寸，567 ≈ 1 厘米），这是 Crystal 的原生单位。

所有工具都接受 `subreport` 参数，以便在子报表内部操作。

**安全性**
- 每次编辑都会先把原始文件复制到其旁边的 `_rptmcp_backup/<name>_<timestamp>.rpt`，然后才原地覆盖文件。传入 `output_path` 可改为写入新文件。
- `delete_formula`、`delete_parameter`、`delete_running_total` 和 `remove_table` 在字段仍被使用时会拒绝执行（字段对象、文本对象中内嵌的字段、公式、选择/条件公式、分组、排序、SQL 命令），并列出其使用位置。`force: true` 会连同绑定的对象一起删除。如果没有这道防护，Crystal 会静默丢弃所有绑定到被删公式的对象。

## 环境要求

- Windows
- .NET Framework 4.8
- **适用于 .NET Framework 的 SAP Crystal Reports 运行时（v13，64 位）**，或 *SAP Crystal Reports for Visual Studio*（建议 SP 20+）。可从 SAP 免费下载。该运行时是 SAP 的专有软件，**不**包含在本仓库中。
- 从源代码构建：.NET SDK 6+（用于构建 `net48`）。

## 安装

1. 安装 SAP Crystal Reports 运行时（见“环境要求”）。
2. 通过以下任一方式获取 rpt-mcp：
   - **Zip：** 从 [Releases](https://github.com/ashafizullah/rpt-mcp/releases) 下载 `rpt-mcp-<version>-win-x64.zip`，解压到任意位置（例如 `C:\Tools\rpt-mcp`），然后用你的 MCP 客户端注册 `rpt-mcp.exe`（见下文）。
   - **MCP Bundle：** 在支持 [MCP Bundles](https://github.com/modelcontextprotocol/mcpb) 的客户端（例如 Claude Desktop）中打开 Releases 里的 `rpt-mcp-<version>.mcpb`。
   - **MCP Registry：** rpt-mcp 已以 `io.github.ashafizullah/rpt-mcp` 收录于[官方 MCP Registry](https://registry.modelcontextprotocol.io)。

## 从源代码构建

```powershell
cd src
dotnet build -c Release
# -> src\bin\Release\rpt-mcp.exe
```

本项目引用来自 GAC 的 Crystal 程序集（`C:\Windows\assembly\GAC_MSIL`）。如果你的程序集在别处，可用 `dotnet build -p:CrGacMsil=<folder>` 覆盖。

对于 32 位 Crystal 运行时，请使用 `-p:PlatformTarget=x86` 构建。

用 `dotnet test RptMcp.sln -c Release` 运行测试。报表测试见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 注册到 MCP 客户端

Claude Code：

```powershell
claude mcp add rpt-mcp -- "C:\path\to\rpt-mcp.exe"
```

Claude Desktop / 其他客户端（`mcpServers` 配置）：

```json
{
  "mcpServers": {
    "rpt-mcp": { "command": "C:\\path\\to\\rpt-mcp.exe" }
  }
}
```

## 数据库连接（可选）

编辑不需要数据库。`export_report`、`set_command_sql` 和 `set_datasource` 通常需要，而 `add_table` 始终需要（Crystal 会从服务器读取列清单）。
直接传入 `server` / `database` / `user` / `password` / `integrated`，或在 exe 旁边创建 `connections.json`（或用 `RPTMCP_CONNECTIONS` 指向它），然后传入 `connection: "<name>"`：

```json
{
  "dev": { "server": "localhost\\SQLEXPRESS", "database": "Northwind", "integrated": true }
}
```

`connections.json` 已被 git 忽略；切勿提交凭据。

## 示例提示词

- “检查 `C:\reports\Invoice.rpt`，把标题改成 *Tax Invoice*，加粗 14pt。”
- “添加一个公式 `{@FullName}` = 名字 + 姓氏，并把它放到详细资料节中客户编号的旁边。”
- “把 `C:\reports` 下所有报表的每一张表都指向服务器 `SQL02`、数据库 `SalesProd`。”
- “把 `(localdb)\MSSQLLocalDB` 中数据库 `Sales` 的表 `dbo.Orders` 添加到空白的 `Orders.rpt`，并把 `OrderNo` 和 `Total` 放到详细资料节中。”
- “按班次对生产报表分组，在组页眉中显示 *Shift : n*，在组页脚中显示 Qty/Good/Reject 小计。”
- “在报表页脚中添加一个按产品汇总的子报表，并链接到报表的日期和班次参数。”
- “把报表设为 A4 横向，并用 RecordNumber 为各行编号。”
- “把 Invoice.rpt 导出为 PDF，带 `OrderNo = 1001`，以便我检查版面。”
- “把 Invoice.rpt 的第 1 页导出为 PNG，查看它，并修复任何重叠的列。”

## 提示

- 旧报表常使用传统的 `SQLOLEDB` 提供程序，它无法连接到 LocalDB 或仅支持 TLS 1.2 的服务器。使用 `set_datasource` 并指定 `provider: "MSOLEDBSQL"`（Microsoft OLE DB Driver for SQL Server）即可解决。

## 限制

- **不支持创建新的 SQL 命令。** 进程内的 Crystal 运行时会以 “Failed to load database information” 拒绝该操作，尽管在同一连接上添加普通表却能成功。`set_command_sql` 只能编辑已存在的命令，且尚未针对包含 SQL 命令的报表进行验证。
- `add_table` 添加表时不带链接；当报表中有多张表时，Crystal 会对它们做交叉联接。目前尚不支持添加表链接。
- 图表、交叉表和 OLAP 网格可以检查和移动，但不能进行结构性编辑。
- 线型：运行时会拒绝 `double`，并把 `dashed`/`dotted` 线条画成极细线。图片以位图存储（透明会被压平成白色）。
- 直接绑定到运行总计的字段对象无法由运行时保存（`SaveAs` 会以 “No error” 失败），因此 `add_field_object` 通过一个文本为 `{#Name}` 的公式 `{@Name}` 来显示 `{#Name}`。`delete_running_total` 也会移除该公式。
- 运行时无法创建空报表，因此新建子报表时会先把它构建为独立的 `.rpt`（例如复制一个报表并用这些工具裁剪它），再用 `add_subreport` 插入。子报表对象无法移动到另一个节；请把它们插入到应在的位置。
- 默认参数值只是提示词中提供的选项；`export_report` 仍需要在 `parameters` 中传入具体值。
- `set_command_sql` 和 `set_datasource` 走的是 Crystal 的表位置 API，它会连接数据库以校验架构。
- 已在 Windows 11 和 SQL Server LocalDB 上使用 Crystal Reports 运行时 13.0.2000（x64）测试通过。

## 协议

基于 stdio 的 JSON-RPC 2.0（换行分隔）。由于 Crystal 运行时的运行环境仅为 .NET Framework，因此本协议为手工实现。日志写入 stderr。

## 许可与法律声明

- rpt-mcp 采用 [MIT 许可证](LICENSE)发布。
- 本项目是一个独立项目，**与 SAP SE 无关联，也未获得其认可或赞助**。SAP 和 Crystal Reports 是 SAP SE 在德国及其他国家的商标或注册商标。此处使用它们仅用于描述兼容性。
- SAP Crystal Reports 运行时**不**随本项目分发。每位用户需自行从 SAP 安装，并受 SAP 许可条款约束。SAP 将该运行时描述为对客户端（桌面）应用免费。rpt-mcp 以本地、单用户进程运行，属于这种部署方式。如果你将其作为多个用户访问的共享服务器运行，请查阅 SAP 针对服务器应用的许可规定（例如 Crystal Reports Developer Advantage 许可证）。
- 第三方组件列于 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
