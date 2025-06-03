#:package Spectre.Console@0.*
#:package YamlDotNet@16.*

using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Diagnostics;

string target = @"../reference/test/example/simple.yml";

IDeserializer deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();

string yaml = await File.ReadAllTextAsync(target);

NoSQLQueryReference data = deserializer.Deserialize<NoSQLQueryReference>(yaml);

AnsiConsole.Write(
    new Panel(target)
        .Header("File")
);
AnsiConsole.Write(
    new Panel(data?.Examples?.Sample?.Query ?? "<no-query-specified>")
        .Header("Query")
);
string command = "db.products.find({})";

string connectionString = "mongodb://localhost:27017/cosmicworks";

ProcessStartInfo configuration = new()
{
    FileName = "mongosh",
    Arguments = $"\"{connectionString}\" --eval \"{command}\"",
    RedirectStandardOutput = true,
    UseShellExecute = false,
    CreateNoWindow = true
};

using Process? process = Process.Start(configuration);
if (process is not null)
{
    string result = await process.StandardOutput.ReadToEndAsync();
    Console.WriteLine(result);
}

record NoSQLQueryReference
{
    public string? Name { get; set; }

    public NoSQLQueryReferenceExampleSet? Examples { get; set; }
}

record NoSQLQueryReferenceExampleSet
{
    public NoSQLQueryReferenceSample? Sample { get; set; }

    public IReadOnlyList<NoSQLQueryReferenceExample>? Examples { get; set; }
}

record NoSQLQueryReferenceSample
{
    public string? Type { get; set; }

    public string? Query { get; set; }
}

record NoSQLQueryReferenceExample
{
    public string? Title { get; set; }

    public string? Query { get; set; }

    public string? Output { get; set; }
}