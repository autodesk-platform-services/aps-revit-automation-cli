# APS Revit Automation CLI

![platforms](https://img.shields.io/badge/platform-windows-lightgray.svg)
[![.net](https://img.shields.io/badge/net-10.0-blue.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![Design-Automation](https://img.shields.io/badge/Design%20Automation-v3-green.svg)](https://aps.autodesk.com/en/docs/design-automation/v3/developers_guide/overview/)
[![license](https://img.shields.io/:license-mit-green.svg)](https://opensource.org/licenses/MIT)

A .NET 10 CLI tool that wraps the Autodesk Platform Services (APS) Design Automation API v3 for Revit workflows. Define a single YAML configuration file describing your AppBundle, Activity, inputs, and outputs, then run `revit run ./job.yaml` — the CLI handles AppBundle packaging and upload, Activity creation, WorkItem submission, polling, and output downloads automatically. Supports Windows x64 and ARM64.

> **Note**: This project was built with [Claude Code](https://claude.ai/code).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An [Autodesk Platform Services](https://aps.autodesk.com/) application (traditional/confidential type) with `code:all`, `data:read`, and `data:write` scopes
- A Revit AppBundle folder containing a `.bundle` subfolder with your plugin

## Installation

### Option A — dotnet tool (recommended)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet tool install -g Autodesk.RevitCli
```

Update to the latest version:

```bash
dotnet tool update -g Autodesk.RevitCli
```

Uninstall:

```bash
dotnet tool uninstall -g Autodesk.RevitCli
```

### Option B — self-contained binary (no SDK required)

Download the latest ZIP for your architecture from [GitHub Releases](https://github.com/autodesk-platform-services/aps-revit-automation-cli/releases):

- `revit-win-x64-v*.zip` — Windows x64
- `revit-win-arm64-v*.zip` — Windows ARM64

Extract the ZIP and add the folder to your `PATH`.

## Commands

### `revit run <yaml-file>`

Executes a complete Revit automation job:

1. Validates the YAML configuration
2. Authenticates (2-legged for setup, 3-legged for model access)
3. Packages and uploads the AppBundle (skips upload if unchanged)
4. Creates or updates the Activity
5. Submits and polls the WorkItem
6. Downloads outputs on success

```bash
revit run ./job.yaml
```

### `revit validate <yaml-file>`

Validates a YAML configuration file without running the job. Checks YAML syntax, required fields, and verifies that `app.path` exists on disk.

```bash
revit validate ./job.yaml
```

### `revit auth login`

Prompts for your APS `clientId` and `clientSecret`, then starts a browser-based 3-legged OAuth login flow. Credentials and tokens are cached in `~/.revit-cli/tokens.json` and refreshed automatically on subsequent runs, so they never need to appear in your `job.yaml`.

```bash
revit auth login
```

### `revit auth status`

Shows the current authentication token status (valid, expired, or missing) and expiry time.

```bash
revit auth status
```

## YAML Configuration

> **Note**
> Credentials are not stored in `job.yaml`. Authenticate once with `revit auth login`; your `clientId`, `clientSecret`, and tokens are cached in `~/.revit-cli/tokens.json`.

See [`examples/job.yaml`](examples/job.yaml) for a complete example.

| Field | Required | Description |
|---|---|---|
| `revit.version` | Yes | Revit version: `latest`, `2022`, `2023`, `2024`, `2025`, `2026`, or `2027`. `latest` resolves to `2027`. |
| `app.name` | Yes | Unique name for the AppBundle and Activity. Must not contain hyphens (the Design Automation API rejects hyphenated AppBundle ids). |
| `app.description` | No | Optional description |
| `app.path` | Yes | Path to the local AppBundle folder (must contain exactly one `.bundle` subfolder) |
| `environment` | No | Alias applied to the AppBundle and Activity. Must be `dev` or `prod`. Defaults to `prod`. |
| `inputs.model.type` | Yes | Must be `cloudWorksharedModel` |
| `inputs.model.folderUrl` | Yes | ACC browser URL to the folder containing the model |
| `inputs.model.modelName` | Yes | Name of the Revit model (without `.rvt` extension) |
| `inputs.model.save` | No | Whether to save/sync the Revit model after processing. Default: `true`. Set to `false` for read-only operations. |
| `inputs.model.openOption` | No | Workset open behavior. One of: `OpenAllWorksets` (default), `CloseAllWorksets`, `CloseWorksetsWithRevitLinks`. |
| `inputs.tool.name` | No | Tool identifier passed to the AppBundle (emitted as `toolName` in `revitmodel.json`). |
| `inputs.tool.inputs` | No | Path to a local JSON file delivered to the AppBundle as `toolinputs.json`. If absent, `toolinputs.json` receives `{}`. |
| `outputs.result.type` | No | Output type (e.g., `file`). Required only if `outputs.result.path` is set. Omit the entire `outputs` section to skip output bucket creation and download. |
| `outputs.result.path` | No | Local path where the output file will be downloaded. Required only if `outputs.result.type` is set. |

## AppBundle ZIP Structure

The `app.path` directory must contain exactly one `.bundle` subfolder. The CLI zips this directory automatically and computes a SHA-256 hash to skip redundant uploads on repeated runs.

```
my-appbundle/
  MyPlugin.bundle/
    Contents/
      MyPlugin.dll
      MyPlugin.addin
      PackageContents.xml
```

## License

[MIT](LICENSE)
