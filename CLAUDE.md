# WhisperDesk

Voice-to-text desktop app (WPF, .NET 9).

## Architecture

### Module Structure

Each domain module lives in its own folder under `src/`, containing a **Contract** project (interfaces + data models) and an **Implementation** project:

```
src/
├── core/
│   └── WhisperDesk.Core/                # Pipeline orchestration, services, non-STT logic
├── ui/
│   └── WhisperDesk/                     # WPF UI layer
├── stt/
│   ├── WhisperDesk.Stt.Contract/    # IStreamingSttProvider, SttResults, SttSessionOptions, AudioFormat
│   ├── providers/
│   │   ├── WhisperDesk.Stt.Provider.Azure/       # Azure Speech implementation
│   │   └── WhisperDesk.Stt.Provider.Volcengine/  # Volcengine Doubao implementation
│   └── WhisperDesk.Stt/             # DI assembly — references all providers, exposes AddSttProvider()
└── llm/
    └── ...
```

### Dependency Rules

- **Contract projects have zero project dependencies.** They only reference `Microsoft.Extensions.*` abstractions if needed.
- **Implementation projects depend on their own Contract**, never on other implementation projects.
- **Core depends on Contract projects** (e.g., `WhisperDesk.Stt.Contract`), never on implementations.
- **UI (WhisperDesk) wires everything together** — it references both Contract and Implementation projects, and calls DI registration extensions.

```
WhisperDesk (UI)
  → WhisperDesk.Core
  → WhisperDesk.Stt            (for DI registration)
  → WhisperDesk.Stt.Contract   (transitive via Core)

WhisperDesk.Core
  → WhisperDesk.Stt.Contract   (interfaces only)

WhisperDesk.Stt
  → WhisperDesk.Stt.Contract
  → WhisperDesk.Stt.Provider.Azure      → WhisperDesk.Stt.Contract
  → WhisperDesk.Stt.Provider.Volcengine  → WhisperDesk.Stt.Contract
```

### Adding a New Module

Follow the same pattern. Example for a future LLM module:

1. Create `src/llm/WhisperDesk.Llm.Contract/` — interfaces (`ILlmProvider`), data models
2. Create `src/llm/providers/WhisperDesk.Llm.Provider.AzureOpenAI/` — implementation
3. Create `src/llm/WhisperDesk.Llm/` — DI assembly, references Contract + all providers
4. Core references `WhisperDesk.Llm.Contract`
5. UI references `WhisperDesk.Llm`, calls `services.AddLlmProvider(...)`

### Naming Conventions

| Item | Convention |
|------|-----------|
| Contract project | `WhisperDesk.<Module>.Contract` |
| Provider project | `WhisperDesk.<Module>.Provider.<Name>` |
| Assembly project | `WhisperDesk.<Module>` (DI composition) |
| Namespace | Matches project name |
| DI extension class | `<Module>ServiceRegistration` in the assembly project |
| DI extension method | `services.Add<Module>Provider(...)` |
| Folder under `src/` | Lowercase module name (e.g., `stt/`, `llm/`) |
| Providers subfolder | `src/<module>/providers/` |

### ProjectReference Rules

**Always use path variables from `Directory.Build.props` for project references.** Never use relative paths like `..\..\` or `..\`.

```xml
<!-- CORRECT -->
<ProjectReference Include="$(CoreRoot)WhisperDesk.Core.Contract\WhisperDesk.Core.Contract.csproj" />
<ProjectReference Include="$(SttRoot)WhisperDesk.Stt.Contract\WhisperDesk.Stt.Contract.csproj" />

<!-- WRONG — never do this -->
<ProjectReference Include="..\WhisperDesk.Core.Contract\WhisperDesk.Core.Contract.csproj" />
```

Available path variables (defined in `Directory.Build.props`):
- `$(SrcRoot)` — `src/`
- `$(CoreRoot)` — `src/core/`
- `$(UiRoot)` — `src/ui/`
- `$(SttRoot)` — `src/stt/`
- `$(LlmRoot)` — `src/llm/`
- `$(ServerRoot)` — `src/server/`
- `$(ProtoRoot)` — `src/proto/`

## Build & Run

```bash
dotnet build                                    # Build all
dotnet run --project src/ui/WhisperDesk/WhisperDesk.csproj  # Run the app
dotnet test                                     # Run tests
```

## Publish

One command, no external dependencies:

```bash
# Production (no env-specific config)
dotnet publish src/ui/WhisperDesk/WhisperDesk.csproj -c Release -o publish -p:DebugType=none

# With environment config (copies appsettings.{Env}.json into publish output)
dotnet publish src/ui/WhisperDesk/WhisperDesk.csproj -c Release -o publish -p:DebugType=none -p:Env=Development
```

Output in `publish/`:
- `WhisperDesk.exe` — self-contained single-file (~87 MB, bundles .NET runtime)
- `appsettings.json` — user-editable config (excluded from single-file)
- `hotwords.json` — phrase hints (excluded from single-file)
