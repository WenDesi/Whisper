# WhisperDesk Coding Conventions

## File Organization

### Config and Data Contracts

Config classes (e.g., `AzureSttConfig`, `VolcengineSttConfig`) and data contract/protocol model classes (e.g., JSON serialization models) must be placed in **separate files** from the implementation that uses them.

- Config classes go in their own file named `[Provider]Config.cs`.
- Protocol/data contract models go in a dedicated file (e.g., `VolcengineProtocolModels.cs`), not inline with the provider implementation.

### Provider Directory Structure

Each provider should have its own directory under `Providers/`:

```
Providers/
  Stt/
    Azure/
      AzureSttConfig.cs
      AzureSttProvider.cs
    Volcengine/
      VolcengineSttConfig.cs
      VolcengineSttProvider.cs
      VolcengineProtocolModels.cs
  Llm/
    AzureOpenAI/
      AzureOpenAILlmConfig.cs
      AzureOpenAILlmProvider.cs
```

## Dependency Injection

### Uniform Provider Config Registration

All provider configs should be treated uniformly in `PipelineServiceRegistration`. No provider should receive special treatment (e.g., one config being nullable while others are required).

- All provider configs are optional/nullable parameters.
- Register only the configs that are provided (non-null).
- Validate inside the provider selection switch: if a provider is selected but its config is missing, throw an `InvalidOperationException` with a clear message.

```csharp
// Good: uniform treatment
public static IServiceCollection AddWhisperDeskPipeline(
    this IServiceCollection services,
    PipelineConfig pipelineConfig,
    AzureSttConfig? azureSttConfig = null,
    AzureOpenAILlmConfig? azureOpenAIConfig = null,
    VolcengineSttConfig? volcengineSttConfig = null)
```
