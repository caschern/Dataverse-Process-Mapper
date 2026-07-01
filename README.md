# Dataverse Process Mapper (XrmToolBox tool)

Lists **Classic Workflows** and **Power Automate flows** from the Dataverse
`workflow` table and renders visual **process maps** that you can export to
**PDF** or **HTML**.

## What it does

- Connects through XrmToolBox and queries the `workflow` table for
  *definition* rows (`type = 1`).
- Two tabs:
  - **Power Automate Flows** — `category = 5`, parsed from the JSON in the
    **`clientdata`** column (trigger + action graph, wired up via `runAfter`,
    recursing into If / Switch / Foreach / Until / Scope).
  - **Classic Workflows** — `category = 0`, parsed from the WF4 XAML in the
    **`xaml`** column (step activities with branching for If / While / Switch).
- Each automation is listed by its **`name`** field.
- Selecting one renders a live, zoomable preview (Ctrl + mouse-wheel to zoom).
- **Generate PDF** produces a vector PDF (PdfSharp).
- **Generate HTML** produces a self-contained HTML file (embedded PNG diagram +
  metadata + step/branch tables).

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
| `XrmToolBox` | Extensibility + connection control | No (host-provided) |
| `Microsoft.CrmSdk.XrmTooling.CoreAssembly` | `IOrganizationService` | No (host-provided) |
| `Newtonsoft.Json` | Parse `clientdata` JSON | No (host-provided) |
| `PdfSharp` (1.50.x) | PDF export | **Yes** |

The host-provided packages use `<ExcludeAssets>runtime</ExcludeAssets>` so they
are not copied to the output (avoiding load conflicts with the XrmToolBox host).

> If `dotnet restore` cannot resolve the floating `XrmToolBox` version, open the
> project in Visual Studio and **Manage NuGet Packages → Updates** to pin the
> latest available version.

## Deploy

Copy these two files into the XrmToolBox `Plugins` folder
(`%AppData%\MscrmTools\XrmToolBox\Plugins`):

- `DataverseProcessMapper.dll`
- `PdfSharp.dll`

Restart XrmToolBox; the tool appears as **Dataverse Process Mapper**.

## Project layout

```
Data/WorkflowRepository.cs      Query the workflow table (paged)
Models/                         ProcessItem, ProcessGraph (nodes/edges)
Parsing/FlowJsonParser.cs       Power Automate clientdata JSON  -> graph
Parsing/XamlWorkflowParser.cs   Classic workflow XAML           -> graph
Layout/NodeSizer.cs             Word-wrap + measure node boxes
Layout/LayeredLayoutEngine.cs   Longest-path layered DAG layout
Rendering/IDiagramSurface.cs    Backend-independent draw primitives
Rendering/GdiDiagramSurface.cs  GDI+ backend (screen + PNG)
Rendering/PdfDiagramSurface.cs  PdfSharp backend (PDF)
Rendering/DiagramRenderer.cs    Shapes, text, arrows (shared logic)
Export/PdfExporter.cs           PDF output
Export/HtmlExporter.cs          HTML output (embedded PNG + tables)
UI/DiagramPanel.cs              Scroll/zoom preview control
ProcessMapperControl.cs         Two-tab plugin UI
ProcessMapperPlugin.cs          MEF entry point (tile metadata)
```

## Known limitations

- The classic-workflow XAML parser is a pragmatic **structural** walker, not a
  full WF4 interpreter. Exported D365 workflow XAML is verbose and varies by
  designer version; the parser extracts the recognisable step/branch structure
  and ignores pure-expression noise. Unrecognised steps are skipped rather than
  guessed. Extend `XamlWorkflowParser.StepActivities` to surface more activity
  types.
- The layout is a lightweight layered algorithm (no edge-crossing minimisation),
  which is clear for mostly-linear processes; very wide graphs may have crossing
  connectors.
- Categories other than Classic Workflow (0) and Modern Flow (5) are not loaded.
  Add them in `WorkflowRepository` and route to a parser in `ProcessMapBuilder`.
