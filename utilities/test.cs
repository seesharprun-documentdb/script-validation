#:package Humanizer@2.*
#:package MSTest@3.*
#:package YamlDotNet@16.*

using Humanizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

[TestClass]
public class QueryLanguageTests
{
    [TestMethod]
    [DynamicData(
        dynamicDataSourceName: nameof(QueryDataSource.GetTestData),
        dynamicDataDeclaringType: typeof(QueryDataSource),
        dynamicDataSourceType: DynamicDataSourceType.Method,
        DynamicDataDisplayName = nameof(QueryDataSource.GetTestDisplayName),
        DynamicDataDisplayNameDeclaringType = typeof(QueryDataSource))]
    public async Task TestQueryAsync(string unit, string query, string? sample, string? output)
    {
        // Arrange
        string command = query;
        string? expected = output?.Trim();
        string database = unit.Kebaberize();

        // Setup
        if (sample is not null && Enum.TryParse(sample, out NoSQLQueryReferenceSampleSet set))
        {
            await set.SeedDataAsync(database);
        }

        // Act
        string response = await command.ExecuteCommandAsync(database);
        string actual = response.Trim();

        // Assert
        if (expected is not null)
        {
            bool match = String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(match, $"The output of query {command} for [{unit}] should match the expected output: {expected}.");
        }

        // Cleanup
        await "db.dropDatabase()".ExecuteCommandAsync(database);
    }

    [ClassInitialize]
    public static async Task InitializeTestsWithinClassAsync(TestContext _)
    {
        string response = await "db.runCommand({ ping: 1 })".ExecuteCommandAsync();
        bool success = response switch
        {
            "{ ok: 1 }" => true, // Connection successful, do nothing.
            null or _ when string.IsNullOrWhiteSpace(response) => false,
            _ => false
        };
        if (!success)
        {
            throw new InvalidOperationException($"Failed to connect to the database. Please check your connection settings. Response: {response}");
        }
    }
}

static class DataSourceExtensions
{
    internal static async Task SeedDataAsync(this NoSQLQueryReferenceSampleSet sampleSet, string database)
    {
        bool filter(string resource) =>
            resource.Contains(".sample", StringComparison.OrdinalIgnoreCase) &&
            resource.Contains($".{sampleSet}".Kebaberize(), StringComparison.OrdinalIgnoreCase) &&
            resource.EndsWith(".mongo", StringComparison.OrdinalIgnoreCase);
        string resource = Assembly.GetExecutingAssembly().FindResources(filter).SingleOrDefault()
            ?? throw new InvalidOperationException($"Resource for sample set '{sampleSet}' not found.");

        using Stream stream = Assembly.GetExecutingAssembly().StreamResource(resource);
        using StreamReader reader = new(stream);
        string command = await reader.ReadToEndAsync();

        await command.ExecuteCommandAsync(database, file: true);
    }
}

static partial class CommandShellExtensions
{
    private static readonly string identity = Environment.GetEnvironmentVariable("DOCUMENTDB_IDENTITY") ?? "devuser";

    private static readonly string credential = Environment.GetEnvironmentVariable("DOCUMENTDB_CREDENTIAL") ?? "P@ssw.rd";

    private static readonly string host = Environment.GetEnvironmentVariable("DOCUMENTDB_HOST") ?? "localhost";

    private static readonly int port = Environment.GetEnvironmentVariable("DOCUMENTDB_PORT") is string portString && int.TryParse(portString, out int portValue) ? portValue : 10260;

    internal static async Task<string> ExecuteCommandAsync(this string command, string? database = null, bool file = false)
    {
        string target = $"mongodb://{host}:{port}{(database is not null ? $"/{database}" : string.Empty)}";

        ProcessStartInfo configuration = new()
        {
            FileName = "mongosh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        configuration.ArgumentList.Add(target);
        configuration.ArgumentList.Add("--username");
        configuration.ArgumentList.Add(identity);
        configuration.ArgumentList.Add("--password");
        configuration.ArgumentList.Add(credential);
        configuration.ArgumentList.Add("--authenticationMechanism");
        configuration.ArgumentList.Add("SCRAM-SHA-256");
        configuration.ArgumentList.Add("--authenticationDatabase");
        configuration.ArgumentList.Add("admin");
        configuration.ArgumentList.Add("--tls");
        configuration.ArgumentList.Add("--tlsAllowInvalidCertificates");
        configuration.ArgumentList.Add("--quiet");

        string tmp = Path.GetTempFileName();
        if (file)
        {
            await File.WriteAllTextAsync(tmp, command);
            configuration.ArgumentList.Add("--file");
            configuration.ArgumentList.Add(tmp);

        }
        else
        {
            configuration.ArgumentList.Add("--eval");
            configuration.ArgumentList.Add(command);
        }

        using Process? process = Process.Start(configuration);
        if (process is not null)
        {
            await process.WaitForExitAsync();
        }
        string output = process switch
        {
            null => throw new InvalidOperationException("Process could not be started."),
            { ExitCode: not 0 } => throw new InvalidOperationException($"Process exited with code {process.ExitCode}. {await process.StandardError.ReadToEndAsync()}"),
            _ => await SanitizeOutputAsync(process.StandardOutput)
        };

        if (file)
        {
            File.Delete(tmp);
        }

        return output;
    }

    private static async Task<string> SanitizeOutputAsync(StreamReader reader) =>
        DeprecationWarningPattern()
            .Replace(await reader.ReadToEndAsync(), string.Empty)
            .Trim();

    [GeneratedRegex(@"^DeprecationWarning:.*$", RegexOptions.Multiline)]
    private static partial Regex DeprecationWarningPattern();
}

static class QueryDataSource
{
    private static readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static IEnumerable<(string example, string query, string? sample, string? output)> GetTestData()
    {
        static bool filter(string resource) =>
            resource.Contains(".reference", StringComparison.OrdinalIgnoreCase) &&
            resource.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
        IEnumerable<string> resources = Assembly.GetExecutingAssembly().FindResources(filter);

        List<NoSQLQueryReference> items = [];
        foreach (string resource in resources)
        {
            using Stream stream = Assembly.GetExecutingAssembly().StreamResource(resource);
            using StreamReader reader = new(stream);
            NoSQLQueryReference data = deserializer.Deserialize<NoSQLQueryReference>(reader)
                ?? throw new InvalidOperationException($"Failed to deserialize resource '{resource}'.");
            items.Add(data);
        }

        foreach (NoSQLQueryReference item in items)
        {
            string? sample = $"{item.Examples.Sample?.Set}";
            foreach (NoSQLQueryReferenceExample example in item.Examples.Items)
            {
                string name = $"{item.Name} {example.Title}".Kebaberize();
                string query = example.Query;
                string? output = example.Output?.Value;
                yield return (name, query, sample, output);
            }
        }
    }

    public static string GetTestDisplayName(MethodInfo methodInfo, object[] data) =>
        BuildDisplayName(methodInfo.Name, (string)data[0], (string?)data[1]);

    private static string BuildDisplayName(string methodName, string unit, string? _) =>
        $"{methodName} {unit}".Kebaberize();
}

static class ResourceExtensions
{
    internal static IEnumerable<string> FindResources(this Assembly assembly, Func<string, bool> filter) =>
        assembly.GetManifestResourceNames()
            .Where(filter);

    internal static Stream StreamResource(this Assembly assembly, string resource) =>
        assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Resource '{resource}' not found.");
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