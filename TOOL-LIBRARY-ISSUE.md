# Tool Library validation reports "XrmToolBox version dependency is missing" for a package whose dependency is identical to a live, working tool

## Summary

When submitting my tool `CasasHern.DataverseProcessMapper` (version `1.0.16`) to the Tool Library, validation fails with:

> Errors have been found in your Nuget package:
> XrmToolBox version dependency is missing in Nuget package.

However, my package **does** declare the `XrmToolBox` dependency, and nuget.org's structured dependency metadata (the `registration5-gz-semver2` API, which is what the portal reads) reports it **identically** to tools that are currently live in the Tool Library.

## Environment

- NuGet package: `CasasHern.DataverseProcessMapper` — https://www.nuget.org/packages/CasasHern.DataverseProcessMapper
- Failing version: `1.0.16`
- Target framework: `net48`
- Built against `XrmToolBoxPackage` `1.2025.10.74` (runtime assets excluded)

## Evidence the package is correct

**1. nuget.org registered dependency (what the portal consumes) is identical to a working tool.**

| Package | Registered dependency |
| --- | --- |
| `casashern.dataverseprocessmapper` **1.0.16** (fails) | `XrmToolBox [1.2025.10.74, )` |
| `cinteros.xrm.fetchxmlbuilder` 1.2026.5.1 (live/works) | `XrmToolBox [1.2025.10.74, )` |
| `mscrmtools.metadatadocumentgenerator` 1.2025.11.9 (live/works) | `XrmToolBox [1.2016.11.4, )` |

Source (returns the dependency group for my package):
`https://api.nuget.org/v3/registration5-gz-semver2/casashern.dataverseprocessmapper/1.0.16.json`

**2. Package layout matches the checklist.**

```
lib/net48/Plugins/DataverseProcessMapper.dll
lib/net48/Plugins/DataverseProcessMapper/PdfSharp.dll   (3rd-party dep in tool subfolder)
```

**3. The plugin assembly references XrmToolBox correctly.**

- `DataverseProcessMapper.dll` (assembly version `1.0.16.0`) references `XrmToolBox.Extensibility 1.2025.10.74`.
- Package version `1.0.16` matches the assembly version.

**4. All validation-checklist items are met** — Icon url, Project url, Plugins folder, version match, large/small tile images (`BigImageBase64`/`SmallImageBase64`), resize handling, disconnected operation, `ExecuteMethod` connection prompting, and `WorkAsync` for long-running operations.

## What I've already tried

I iterated through many versions fixing genuinely-wrong earlier packages:
- Removed a stray `XrmToolBoxPackage` dependency (present in my old 1.0.8).
- Corrected the dependency id to `XrmToolBox`.
- Placed the plugin assembly under `Plugins` with the 3rd-party dependency in a tool subfolder.
- Added `summary`/`releaseNotes` and removed the SPDX `<license>` element.

From `1.0.15` onward the package has been byte-for-byte comparable to FetchXML Builder's dependency metadata, yet validation still reports the dependency as missing.

## Question / request

Given that `1.0.16`'s registered `XrmToolBox` dependency is identical to `Cinteros.Xrm.FetchXMLBuilder` (which is live in the Tool Library), why does validation report it as missing?

Is it possible the validator is checking a **previously-registered older version** of my tool (my `1.0.2`/`1.0.8` genuinely had wrong dependencies) rather than the latest `1.0.16`? If so, how do I reset/re-point the existing registration to validate `1.0.16`?

Thanks for any guidance.
