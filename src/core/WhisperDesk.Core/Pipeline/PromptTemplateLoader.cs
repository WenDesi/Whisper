using System.Reflection;
using Fluid;

namespace WhisperDesk.Core.Pipeline;

internal static class PromptTemplateLoader
{
    private static readonly FluidParser Parser = new();

    /// <summary>
    /// Load a Liquid template embedded as a resource under the Prompts/ folder
    /// of the calling project. Resource id convention follows MSBuild defaults:
    /// "{RootNamespace}.Prompts.{name}".
    /// </summary>
    public static IFluidTemplate Load(Assembly assembly, string name)
    {
        var rootNamespace = assembly.GetName().Name;
        var resourceName = $"{rootNamespace}.Prompts.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var templateText = reader.ReadToEnd();

        return Parser.Parse(templateText)
            ?? throw new InvalidOperationException($"Failed to parse prompt template: {name}");
    }
}
