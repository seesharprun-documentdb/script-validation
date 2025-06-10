#:package AutoMapper@14.*
#:package Spectre.Console@0.*
#:package Stubble.Compilation@1.*
#:package YamlDotNet@16.*

using AutoMapper;
using Spectre.Console;
using System.Reflection;
using Stubble.Compilation;
using Stubble.Compilation.Builders;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

MapperConfiguration mapperConfiguration = new(config => config.CreateMap<NoSQLQueryReference, Payload>());
IMapper mapper = mapperConfiguration.CreateMapper();

IDeserializer yamlDeserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .WithCaseInsensitivePropertyMatching()
    .IgnoreUnmatchedProperties()
    .Build();

using Stream templateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("utilities.templates.reference.mustache.tmpl")
    ?? throw new InvalidOperationException("Template resource not found.");
using StreamReader templateReader = new(templateStream);
string template = await templateReader.ReadToEndAsync();

StubbleCompilationRenderer compiler = new StubbleCompilationBuilder()
    .Configure(settings =>
    {
        settings.SetIgnoreCaseOnKeyLookup(true);
    })
    .Build();

Func<Payload, string> renderer = await compiler.CompileAsync<Payload>(template);

static bool filter(string resource) =>
    resource.Contains(".reference", StringComparison.OrdinalIgnoreCase) &&
    resource.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

IEnumerable<string> resources = Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(filter);

Tree tree = new("[bold yellow]Generating Markdown files...[/]");

foreach (string resource in resources)
{
    TreeNode node = tree.AddNode($"[green]Reading [italic]{resource}[/][/]");

    using Stream yamlStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException($"Resource '{resource}' not found.");
    using StreamReader yamlReader = new(yamlStream);
    NoSQLQueryReference reference = yamlDeserializer.Deserialize<NoSQLQueryReference>(yamlReader)
        ?? throw new InvalidOperationException($"Failed to deserialize resource '{resource}'.");

    Payload payload = mapper.Map<Payload>(reference) with
    {
        Date = $"{DateTime.UtcNow.Date:MM/dd/yyyy}",
        Resources = reference.Related?.Select(r => new PayloadResource { Title = Extensions.ReferenceSplitRegex().Match(r.Reference).Value, Link = r.Reference }) ?? [],
        RenderParameters = reference.Parameters?.Any() ?? false,
        RenderExamples = reference.Examples?.Items?.Any() ?? false,
        UseSample = reference.Examples?.Sample is not null,
    };
    string output = renderer(payload).Trim();

    string outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");

    if (!Directory.Exists(outDir))
    {
        Directory.CreateDirectory(outDir);
    }

    string outFile = Path.Combine(outDir, $"{resource.Replace(".yml", ".generated.md", StringComparison.OrdinalIgnoreCase)}");

    string relativeOutFile = Path.GetRelativePath(Directory.GetCurrentDirectory(), outFile);
    node.AddNode($"[blue]Writing to [italic]{relativeOutFile}[/][/]");

    using FileStream fileStream = File.Open(outFile, FileMode.Create, FileAccess.Write, FileShare.Read);
    using StreamWriter fileWriter = new(fileStream);
    await fileWriter.WriteAsync(output);
}

AnsiConsole.Write(tree);

partial class Extensions
{
    [System.Text.RegularExpressions.GeneratedRegex(@"[^/\\]+$")]
    internal static partial System.Text.RegularExpressions.Regex ReferenceSplitRegex();
}

record Payload : NoSQLQueryReference
{
    public required string Date { get; init; }

    public required IEnumerable<PayloadResource> Resources { get; init; }

    public required bool RenderParameters { get; init; }

    public required bool RenderExamples { get; init; }

    public required bool UseSample { get; init; }
}

record PayloadResource
{
    public required string Title { get; init; }

    public required string Link { get; init; }
}

record NoSQLQueryReference
{
    public required NoSQLQueryReferenceType Type { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string? Summary { get; init; }

    public required string Syntax { get; init; }

    public required IEnumerable<NoSQLQueryReferenceParameter> Parameters { get; init; }

    public required NoSQLQueryReferenceExampleSet Examples { get; init; }

    public required IEnumerable<NoSQLQueryReferenceRelated> Related { get; init; }
}

enum NoSQLQueryReferenceType
{
    Operator = default,

    Command
}

record NoSQLQueryReferenceParameter
{
    public required string Name { get; init; }

    public required NoSQLQueryReferenceParameterType Type { get; init; }

    public required bool Required { get; init; }

    public required string? Description { get; init; }
}

enum NoSQLQueryReferenceParameterType
{
    Object = default,

    String,

    Number,

    Pattern
}

record NoSQLQueryReferenceExampleSet
{
    public required NoSQLQueryReferenceSample? Sample { get; init; }

    public required IEnumerable<NoSQLQueryReferenceExample> Items { get; init; }
}

record NoSQLQueryReferenceSample
{
    public required NoSQLQueryReferenceSampleSet Set { get; init; }

    public required string Filter { get; init; }
}

enum NoSQLQueryReferenceSampleSet
{
    Products = default,

    Stores,

    Employees
}

record NoSQLQueryReferenceExample
{
    public required string Title { get; init; }

    public required string? Explanation { get; init; }

    public required string Description { get; init; }

    public required string Query { get; init; }

    public required NoSQLQueryReferenceExampleOutput? Output { get; init; }
}

record NoSQLQueryReferenceExampleOutput
{
    public required NoSQLQueryReferenceExampleOutputDevLang DevLang { get; init; }

    public required string Value { get; init; }
}

enum NoSQLQueryReferenceExampleOutputDevLang
{
    Bson = default,

    Json,

    PlainText
}

record NoSQLQueryReferenceRelated
{
    public required string Reference { get; init; }
}