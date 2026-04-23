# WhisperDesk

Voice-to-text desktop app (WPF, .NET 9).

## Architecture

### Module Structure

Each domain module lives in its own folder under `src/`, containing a **Contract** project (interfaces + data models) and an **Implementation** project:

```
src/
├── stt/
│   ├── WhisperDesk.Stt.Contract/    # IStreamingSttProvider, SttResults, SttSessionOptions, AudioFormat
│   └── WhisperDesk.Stt/             # Azure, Volcengine implementations + DI registration
├── WhisperDesk.Core/                # Pipeline orchestration, services, non-STT logic
└── WhisperDesk/                     # WPF UI layer
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
  → WhisperDesk.Stt.Contract   (implements the interfaces)
```

### Adding a New Module

Follow the same pattern. Example for a future LLM module:

1. Create `src/llm/WhisperDesk.Llm.Contract/` — interfaces (`ILlmProvider`), data models
2. Create `src/llm/WhisperDesk.Llm/` — implementations (AzureOpenAI, etc.) + `LlmServiceRegistration`
3. Core references `WhisperDesk.Llm.Contract`
4. UI references both `WhisperDesk.Llm.Contract` and `WhisperDesk.Llm`, calls `services.AddLlmProvider(...)`

### Naming Conventions

| Item | Convention |
|------|-----------|
| Contract project | `WhisperDesk.<Module>.Contract` |
| Implementation project | `WhisperDesk.<Module>` |
| Namespace | Matches project name |
| DI extension class | `<Module>ServiceRegistration` in the implementation project |
| DI extension method | `services.Add<Module>Provider(...)` |
| Folder under `src/` | Lowercase module name (e.g., `stt/`, `llm/`) |

## Build & Run

```bash
dotnet build                                    # Build all
dotnet run --project src/WhisperDesk/WhisperDesk.csproj  # Run the app
dotnet test                                     # Run tests
dotnet publish -c Release                       # Self-contained single-file publish
```
