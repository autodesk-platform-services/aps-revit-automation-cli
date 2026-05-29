# aps-revit-automation-cli

A .NET 10 console CLI that wraps the Autodesk Design Automation API v3 for Revit workflows. Define a single YAML file and run `revit run ./job.yaml` — the CLI handles AppBundles, Activities, WorkItems, and output downloads automatically.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An [Autodesk Platform Services](https://aps.autodesk.com/) application (traditional/confidential type) with `code:all`, `data:read`, and `data:write` scopes
- A Revit AppBundle folder containing a `.bundle` subfolder with your plugin

## Installation

```bash
dotnet publish src/RevitCli -c Release -o publish
```

The published `revit` executable will be in the `publish/` directory. Add it to your `PATH` for convenience.

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

Starts a browser-based 3-legged OAuth login flow. Tokens are cached in `~/.revit-cli/tokens.json` and refreshed automatically on subsequent runs.

```bash
revit auth login
```

### `revit auth status`

Shows the current authentication token status (valid, expired, or missing) and expiry time.

```bash
revit auth status
```

## YAML Configuration

See [`examples/job.yaml`](examples/job.yaml) for a complete example.

| Field | Required | Description |
|---|---|---|
| `authentication.clientId` | Yes | APS application client ID |
| `authentication.clientSecret` | Yes | APS application client secret |
| `revit.version` | Yes | Revit version: `latest`, `2022`, `2023`, `2024`, `2025`, `2026`, or `2027`. `latest` resolves to `2027`. |
| `app.name` | Yes | Unique name for the AppBundle and Activity. Must not contain hyphens (the Design Automation API rejects hyphenated AppBundle ids). |
| `app.description` | No | Optional description |
| `app.path` | Yes | Path to the local AppBundle folder (must contain exactly one `.bundle` subfolder) |
| `inputs.model.type` | Yes | Must be `cloudWorksharedModel` |
| `inputs.model.folderUrl` | Yes | ACC browser URL to the folder containing the model |
| `inputs.model.modelName` | Yes | Name of the Revit model (without `.rvt` extension) |
| `inputs.params` | No | Key-value pairs passed to the AppBundle as `params.json` |
| `outputs.result.type` | Yes | Output type (e.g., `file`) |
| `outputs.result.path` | Yes | Local path where the output file will be downloaded |

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
