# Dataverse Process Mapper (XrmToolBox tool)

Lists **Classic Workflows** and **Power Automate flows** from the Dataverse
`workflow` table and renders visual **process maps** that you can export to
**PDF**, **HTML**, **SVG**, **PNG** or **Markdown**.

## What it does

- Connects through XrmToolBox and queries the `workflow` table for
  *definition* rows (`type = 1`).
- Two tabs:
  - **Power Automate flows** — `category = 5`, parsed from the JSON in the
    **`clientdata`** column (trigger + action graph, wired up via `runAfter`,
    recursing into If / Switch / Foreach / Until / Scope).
  - **Classic workflows** — `category = 0`, parsed from the WF4 XAML in the
    **`xaml`** column (designer steps, conditions, waits, stages).
- **Main toolbar:** Load / Reload (F5), a **Solution** filter, **Export ▾** (PDF,
  HTML, SVG for Visio, PNG, Markdown for AI knowledge) for the map shown,
  **Export all ▾** (HTML documentation pack, Markdown knowledge pack) for the
  checked processes or the whole list, and **Open in Power Automate** (or the
  classic workflow designer) with **Copy link**.
- **Process lists:** sortable columns (including when it was modified and what
  triggers it), checkboxes for Export all, a right-click menu, and a filter that
  searches names or **inside every definition** (where is this column, table,
  action or URL used?).
- **Map strip** above each map: find steps (Ctrl+F, F3 / Shift+F3, match count),
  nested or flat layout, expand / collapse all, fit, zoom (Ctrl+0, Ctrl+Plus,
  Ctrl+Minus) and full screen (Esc to leave).
- Click a step to see its full inputs; send a fetchXml to FetchXML Builder.
- Secrets typed into actions (keys, tokens, passwords) show as `(hidden)`.
- Help, Feedback (GitHub issues) and About appear in XrmToolBox's Help menu;
  counts and progress appear in the XrmToolBox status bar.

> **Why two parsers?** Classic workflows and modern flows store their
> definitions in *different columns and formats*: classic workflows use WF4
> **XAML** (`xaml`), while Power Automate flows use **JSON** (`clientdata`).
> The tool detects the category and routes to the correct parser automatically.

## Build

Requirements: Visual Studio 2022 (or `dotnet` SDK + MSBuild) with the
.NET Framework 4.8 targeting pack.

```powershell
dotnet restore
dotnet build -c Release
```

NuGet resolves:

| Package | Role | Deployed with plugin? |
|---|---|---|
| `XrmToolBoxPackage` | Extensibility + connection control | No (host-provided) |
| `Microsoft.CrmSdk.XrmTooling.CoreAssembly` | `IOrganizationService` | No (host-provided) |
| `Newtonsoft.Json` | Parse `clientdata` JSON | No (host-provided) |
| `PdfSharp` (1.50.x) | PDF export | **Yes** |

The host-provided packages use `<ExcludeAssets>runtime</ExcludeAssets>` so they
are not copied to the output (avoiding load conflicts with the XrmToolBox host).

> If `dotnet restore` cannot resolve the `XrmToolBoxPackage` version, open the
> project in Visual Studio and **Manage NuGet Packages → Updates** to pin the
> latest available version.

## Deploy

Install it from the XrmToolBox Tool Library, or copy these files into the
XrmToolBox `Plugins` folder (`%AppData%\MscrmTools\XrmToolBox\Plugins`):

- `DataverseProcessMapper.dll`
- `DataverseProcessMapper\PdfSharp.dll`
- `DataverseProcessMapper\THIRD-PARTY-NOTICES.md`

Restart XrmToolBox; the tool appears as **Dataverse Process Mapper**.

## Project layout

```
Data/WorkflowRepository.cs      Query the workflow table (paged), load definitions in batches
Data/SolutionRepository.cs      Solutions and their processes (solution filter)
Data/DefinitionStore.cs         Background definition cache: trigger summaries, definition search
Data/MakerLinks.cs              Power Automate / workflow designer links per cloud
Models/                         ProcessItem, ProcessGraph (nodes/edges), ExportFormat
Parsing/FlowJsonParser.cs       Power Automate clientdata JSON  -> graph
Parsing/XamlWorkflowParser.cs   Classic workflow XAML           -> graph
Parsing/TriggerSummary.cs       "What starts it" text for flows and classic workflows
Layout/NestedLayoutEngine.cs    Default layout: containers as frames, laid out recursively
Layout/LayeredLayoutEngine.cs   Layered DAG layout (flat mode and inside each frame)
Layout/NodeSizer.cs             Word-wrap + measure node boxes
Rendering/                      Shared drawing logic with GDI+, PdfSharp and SVG backends
Export/                         PDF, HTML, SVG, Markdown and bulk pack exporters
UI/ProcessListPane.cs           Process list: filter, definition search, sorting, checkboxes, menu
UI/MapToolStrip.cs              Find, layout, expand/collapse, fit, zoom, full screen above a map
UI/DiagramPanel.cs              Scroll/zoom/find preview control
UI/NodeDetailsPane.cs           Selected step's details
UI/IconFactory.cs               Fluent UI icons drawn from embedded vector data
ProcessMapperControl.cs         Two-tab plugin UI and main toolbar
ProcessMapperPlugin.cs          MEF entry point (tile metadata)
```

## Known limitations

- The classic-workflow XAML parser is a pragmatic **structural** walker, not a
  full WF4 interpreter. Exported D365 workflow XAML is verbose and varies by
  designer version; the parser extracts the recognisable step/branch structure
  and ignores pure-expression noise. Unrecognised steps are skipped rather than
  guessed. Extend `XamlWorkflowParser.StepActivities` to surface more activity
  types.
- "Open in Power Automate" needs the environment id, which some connection
  types do not provide; the button then says why it is unavailable.
- Categories other than Classic Workflow (0) and Modern Flow (5) are not loaded.
  Add them in `WorkflowRepository` and route to a parser in `ProcessMapBuilder`.
