# Guide for your Claude agent

The ACE Revit MCP server already gives Claude detailed working instructions automatically
(`mcp-server/instructions.md`, sent at connection time). This page gives two optional extras.

## A. Custom instructions for Claude Desktop (recommended)

Put this in **Claude Desktop → Settings → Profile → "What personal preferences should Claude
consider in responses?"**, or in the instructions of a **Project** you use for Revit work:

```
When I ask for anything in Revit, use the ace-revit tools (not other Revit connectors) unless I name another.
Follow the ace-revit safety protocol strictly: understand the model first, explain your plan in plain words,
preview with dry_run and show me what would change, wait for my yes before applying, then verify and tell me how to undo.
Before writing Revit API code you're not sure about, use revit_api_lookup; for hard tasks read revit_guide first.
Our standards: units = <mm / feet>, levels are named like "<L01>", door marks like "<D-001>", sheet numbers like "<A-101>".
Answer in <English / Arabic>. Keep explanations short and practical.
If something in the tool fails or behaves oddly, run check_setup, and offer to create a report with report_issue.
```

Replace the `<...>` parts with your office standards. The more standards you give (naming,
parameters you use, title blocks, view templates), the better Claude's defaults will be.

## B. The tools Claude has

| Area | Tools |
|---|---|
| Understand | `revit_status`, `get_model_overview`, `get_selection`, `find_elements`, `get_element_details`, `describe_category` (real parameters, % filled), `list_types` (loaded families/types), `list_views`, `view_image` (sees views) |
| Knowledge | `revit_api_lookup` (the real Revit API on this PC: signatures, overloads, enums), `revit_guide` (expert guides: planning, performance, transactions, geometry, families, views and sheets, parameters and units, MEP and structure, links and worksharing) |
| Change (gated) | `execute_revit_code` (full Revit API in C#), `set_parameters`, `run_saved_script`, `select_elements` |
| Safety | `backup_model`, `undo_last_claude_change`, `get_activity_log` |
| Library | `list_saved_scripts`, `read_saved_script`, `save_script` (personal or team) |
| Support | `check_setup`, `report_issue` |
| Prompts | *Do a Revit task*, *Model QA check*, *Report a problem* |

Built-in scripts: `audit_model`, `parameter_completeness`, `rooms_without_doors`, `door_width_check`,
`renumber_rooms`, `tag_untagged`, `grid_system`, `sheets_for_levels`, `copy_parameter`.

## C. For Claude Code users (maintainers)

The repository's `CLAUDE.md` tells Claude Code how to work on the tool and how to turn issue
reports into fixes: give it a report with *"Work on this ACE Revit MCP report and improve the tool."*
