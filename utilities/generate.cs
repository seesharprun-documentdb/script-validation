#:package Humanizer@2.*
#:package Microsoft.Extensions.DependencyInjection@9.*
#:package Spectre.Console@0.*
#:package Spectre.Console.Cli@0.*
#:package Spectre.Console.Json@0.*

using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Serialization;

ServiceCollection registrations = new();

registrations.AddSingleton((_) => AnsiConsole.Create(new AnsiConsoleSettings()));
registrations.AddSingleton(new XmlSerializer(typeof(TrxTestRun), new XmlRootAttribute("TestRun") { Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010" }));

registrations.AddTransient<TrxReaderService>();
registrations.AddTransient<ReportTransformerService>();
registrations.AddTransient<CtrfWriterService>();

ITypeRegistrar registrar = new ServiceCollectionRegistrar(registrations);

CommandApp app = new(registrar);

app.SetDefaultCommand<GenerateCommand>();

return await app.RunAsync(args);

sealed class GenerateCommand(
    IAnsiConsole console,
    XmlSerializer xmlSerializer,
    TrxReaderService trxReaderService,
    ReportTransformerService reportTransformerService,
    CtrfWriterService ctrfWriterService
) : AsyncCommand<GenerateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GenerateSettings settings)
    {
        long launchTimestamp = Stopwatch.GetTimestamp();

        Table table = new Table()
            .Collapse()
            .Border(TableBorder.Rounded)
            .AddColumn(string.Empty)
            .AddColumn("[bold]Value[/]")
            .ShowRowSeparators();

        table.AddRow("[bold]Target file[/]", $"[italic]{settings.Target.FullName}[/]");
        table.AddRow("[bold]Output File[/]", $"[italic]{settings.Destination.FullName}[/]");
        table.AddRow("[bold]Source Tool[/]", $"[italic]{settings.ToolName}[/]");

        console.Write(new Panel(table)
            .Header("[yellow bold]Generation Settings[/]")
            .Collapse()
            .RoundedBorder());

        TrxTestRun input = trxReaderService.Read(settings.Target);

        using StringWriter writer = new();
        using XmlWriter xmlWriter = XmlWriter.Create(writer, new()
        {
            Indent = true
        });
        xmlSerializer.Serialize(xmlWriter, input);

        console.Write(new Panel(
            writer.ToString()
        )
            .Header("[yellow bold]Input [italic](TRX)[/][/]")
            .Collapse()
            .RoundedBorder());

        CtrfReport output = reportTransformerService.Transform(input, settings);

        console.Write(new Panel(
            new JsonText(
                JsonSerializer.Serialize(output, StaticJsonSerializerContext.Default.CtrfReport)
            )
                .BracesColor(Color.White)
                .BracketColor(Color.White)
        )
            .Header("[yellow bold]Output [italic](CTRF)[/][/]")
            .Collapse()
            .RoundedBorder());

        ctrfWriterService.Write(settings.Destination, output);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(launchTimestamp);

        console.Write(new Panel(
            new JsonText(
                JsonSerializer.Serialize(new PerformanceProfile
                {
                    Success = true,
                    Runtime = elapsed.Humanize()
                }, StaticJsonSerializerContext.Default.PerformanceProfile)
            )
                .BracesColor(Color.White)
                .BracketColor(Color.White)
        )
            .Header("[yellow bold]Profiler[/]")
            .Collapse()
            .RoundedBorder());

        await Task.CompletedTask;

        return 0;
    }
}

sealed class TrxReaderService(
    XmlSerializer xmlSerializer
)
{
    public TrxTestRun Read(FileInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using FileStream stream = target.OpenRead();

        return xmlSerializer.Deserialize(stream) as TrxTestRun
            ?? throw new InvalidOperationException($"Failed to deserialize TRX file: {target.FullName}");
    }
}

sealed class ReportTransformerService
{
    public CtrfReport Transform(TrxTestRun run, GenerateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(settings);

        return new CtrfReport()
        {
            ReportFormat = "ctrf",
            SpecVersion = "0.0.0",
            ReportId = run.Id,
            Timestamp = $"{DateTimeOffset.UtcNow:O}",
            GeneratedBy = $"{Assembly.GetExecutingAssembly().GetName().Name}",
            Results = new CtrfResults
            {
                Tool = new CtrfTool
                {
                    Name = $"{settings.ToolName}",
                    Version = $"{Assembly.GetExecutingAssembly().GetName().Version}",
                    Extra = new CtrfToolExtra
                    {
                        Settings = new CtrfToolExtraSettings
                        {
                            TrxFile = Path.GetRelativePath(Directory.GetCurrentDirectory(), settings.Target.FullName),
                            CtrfFile = Path.GetRelativePath(Directory.GetCurrentDirectory(), settings.Destination.FullName),
                            Tool = $"{settings.ToolName}"
                        }
                    }
                },
                Summary = new CtrfSummary
                {
                    Tests = run.ResultSummary.Counters.Total,
                    Passed = run.ResultSummary.Counters.Passed,
                    Failed = run.ResultSummary.Counters.Failed,
                    Pending = run.ResultSummary.Counters.Pending,
                    Skipped = run.ResultSummary.Counters.Skipped,
                    Other = run.ResultSummary.Counters.Total - (run.ResultSummary.Counters.Passed + run.ResultSummary.Counters.Failed + run.ResultSummary.Counters.Pending + run.ResultSummary.Counters.Skipped),
                    Suites = run.TestLists.Items.Length,
                    Start = run.Times.Start.ToUnixTimeMilliseconds(),
                    Stop = run.Times.Finish.ToUnixTimeMilliseconds(),
                    Extra = new CtrfSummaryExtra
                    {
                        Created = run.Times.Creation.ToUnixTimeMilliseconds()
                    }
                },
                Tests = [.. run.Results.Items.Select(result => new CtrfTest
                {
                    Name = result.TestName,
                    Status = result.Outcome switch
                    {
                        TrxUnitTestResultOutcome.Passed => CtrfTestStatus.Passed,
                        TrxUnitTestResultOutcome.Failed => CtrfTestStatus.Failed,
                        TrxUnitTestResultOutcome.Skipped => CtrfTestStatus.Skipped,
                        TrxUnitTestResultOutcome.Pending => CtrfTestStatus.Pending,
                        _ => CtrfTestStatus.Other
                    },
                    Duration = Convert.ToInt64(result.DurationTimeSpan.TotalMilliseconds),
                    Suite = run.TestLists.Items.SingleOrDefault(list => list.Id == result.TestListId)?.Name.Kebaberize(),
                    Start = result.StartTime.ToUnixTimeMilliseconds(),
                    Stop = result.EndTime.ToUnixTimeMilliseconds(),
                    Type = "unit",
                    Flaky = false,
                    Extra = new CtrfTestExtra
                    {
                        ExecutionId = result.ExecutionId
                    },
                })]
            },
            Extra = new CtrfReportExtra
            {
                DotNetVersion = $"{Environment.Version}",
            }
        };
    }
}

sealed class CtrfWriterService
{
    public void Write(FileInfo destination, CtrfReport report)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);

        if (destination.Directory is not null && (!destination.Directory?.Exists ?? false))
        {
            destination.Directory?.Create();
        }

        using FileStream stream = destination.Open(FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new(stream);

        string json = JsonSerializer.Serialize(report, StaticJsonSerializerContext.Default.CtrfReport);

        writer.Write(json);
    }
}

sealed class ServiceCollectionRegistrar(
    IServiceCollection serviceCollection
) : ITypeRegistrar
{
    public ITypeResolver Build() => new TypeResolver(serviceCollection.BuildServiceProvider());

    public void Register(Type service, Type implementation) => serviceCollection.AddTransient(service, implementation);

    public void RegisterInstance(Type service, object implementation) => serviceCollection.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        serviceCollection.AddSingleton(service, _ => factory());
    }
}

sealed class TypeResolver(
    IServiceProvider serviceProvider
) : ITypeResolver
{
    public object? Resolve(Type? type) =>
        (type is null) ? null
        : serviceProvider.GetService(type) ?? throw new InvalidOperationException($"Type '{type.FullName}' could not be resolved.");
}

sealed class GenerateSettings : CommandSettings
{
    [Description("Required. The target test results (TRX) file to process.")]
    [CommandArgument(0, "[TARGET]")]
    public required FileInfo Target { get; init; }

    [Description("Optional. The path to the directory where the output files will be written. Defaults to the \"ctrf\" directory.")]
    [CommandOption("-d|--output-directory <OUTPUT_DIRECTORY>")]
    [DefaultValue("ctrf")]
    public required DirectoryInfo OutputDirectory { get; init; }

    [Description("Optional. The filename for the output file. Defaults to \"ctrf-report.json\".")]
    [CommandOption("-f|--output-filename <OUTPUT_FILENAME>")]
    [DefaultValue("ctrf-report.json")]
    public required FileInfo OutputFilename { get; init; }

    internal FileInfo Destination => new(Path.Combine(OutputDirectory.FullName, OutputFilename.Name));

    [ToolNameDescription]
    [CommandOption("-t|--test-tool <TEST_TOOL>")]
    [DefaultValue(GenerateSettingsTestTool.DotNet)]
    public required GenerateSettingsTestTool ToolName { get; init; }

    public override ValidationResult Validate() =>
        this switch
        {
            { Target: null } => ValidationResult.Error("A target file must be specified."),
            { } when string.IsNullOrWhiteSpace(Target.FullName) => ValidationResult.Error("A valid target file must be specified."),
            { OutputDirectory: null } => ValidationResult.Error("An output directory must be specified."),
            { } when string.IsNullOrWhiteSpace(OutputDirectory.FullName) => ValidationResult.Error("A valid output directory must be specified."),
            { OutputFilename: null } => ValidationResult.Error("An output filename must be specified."),
            { } when string.IsNullOrWhiteSpace(OutputFilename.FullName) => ValidationResult.Error("A valid output filename must be specified."),
            _ => ValidationResult.Success()
        };

    [AttributeUsage(AttributeTargets.Property)]
    class ToolNameDescriptionAttribute : DescriptionAttribute
    {
        public override string Description => $"Optional. The test tool used to generate the unit test. Defaults to \"dotnet\". Valid values are: {string.Join(", ", Enum.GetNames<GenerateSettingsTestTool>())}";
    }
}

enum GenerateSettingsTestTool
{
    [JsonStringEnumMemberName("dotnet")]
    DotNet = default,

    [JsonStringEnumMemberName("vstest")]
    VSTest,

    [JsonStringEnumMemberName("nunit")]
    NUnit,

    [JsonStringEnumMemberName("xunit")]
    XUnit,

    [JsonStringEnumMemberName("mstest")]
    MSTest
}

record CtrfReport
{
    public required string ReportFormat { get; init; }

    public required string SpecVersion { get; init; }

    public required string ReportId { get; init; }

    public required string Timestamp { get; init; }

    public required string GeneratedBy { get; set; }

    public required CtrfResults Results { get; init; }

    public CtrfReportExtra? Extra { get; init; }
}

record CtrfReportExtra
{
    public required string DotNetVersion { get; init; }
}

record CtrfResults
{
    public required CtrfTool Tool { get; init; }

    public required CtrfSummary Summary { get; init; }

    public required IEnumerable<CtrfTest> Tests { get; init; } = [];
}

record CtrfTool
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public CtrfToolExtra? Extra { get; init; }
}

record CtrfToolExtra
{
    public required CtrfToolExtraSettings Settings { get; init; }
}

record CtrfToolExtraSettings
{
    public required string TrxFile { get; init; }

    public required string CtrfFile { get; init; }

    public required string Tool { get; init; }
}

record CtrfSummary
{
    public required int Tests { get; init; }

    public required int Passed { get; init; }

    public required int Failed { get; init; }

    public required int Pending { get; init; }

    public required int Skipped { get; init; }

    public required int Other { get; init; }

    public required int Suites { get; init; }

    public required long Start { get; init; }

    public required long Stop { get; init; }

    public CtrfSummaryExtra? Extra { get; init; }
}

record CtrfSummaryExtra
{
    public long? Created { get; init; }
}

record CtrfTest
{
    public required string Name { get; init; }

    public required CtrfTestStatus Status { get; init; }

    public required long Duration { get; init; }

    public long? Start { get; init; }

    public long? Stop { get; init; }

    public string? Suite { get; init; }

    public string? Message { get; init; }

    public string? Trace { get; init; }

    public string? Type { get; init; }

    public bool? Flaky { get; init; }

    public string? StdOut { get; init; }

    public string? StdErr { get; init; }

    public CtrfTestExtra? Extra { get; init; }
}

record CtrfTestExtra
{
    public string? ExecutionId { get; init; }
}

enum CtrfTestStatus
{
    [JsonStringEnumMemberName("other")]
    Other = default,

    [JsonStringEnumMemberName("passed")]
    Passed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("skipped")]
    Skipped,

    [JsonStringEnumMemberName("pending")]
    Pending
}

record PerformanceProfile
{
    public required bool Success { get; init; }

    public required string Runtime { get; init; }
}

[XmlRoot("TestRun", Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")]
public record TrxTestRun
{
    [XmlAttribute("id")]
    public required string Id { get; init; }

    [XmlElement("Times")]
    public required TrxTimes Times { get; init; }

    [XmlElement("TestEntries")]
    public required TrxTestEntries TestEntries { get; init; }

    [XmlElement("Results")]
    public required TrxResults Results { get; init; }

    [XmlElement("TestLists")]
    public required TrxTestLists TestLists { get; init; }

    [XmlElement("ResultSummary")]
    public required TrxResultSummary ResultSummary { get; init; }
}

public record TrxTestEntries
{
    [XmlElement("TestEntry")]
    public required TrxTestEntry[] Items { get; init; }
}

public record TrxTestEntry
{
    [XmlAttribute("testId")]
    public required string TestId { get; init; }

    [XmlAttribute("executionId")]
    public required string ExecutionId { get; init; }

    [XmlAttribute("testListId")]
    public required string TestListId { get; init; }
}

public record TrxResults
{
    [XmlElement("UnitTestResult")]
    public required TrxUnitTestResult[] Items { get; init; }
}

public record TrxUnitTestResult
{
    [XmlAttribute("testId")]
    public required string TestId { get; init; }

    [XmlAttribute("testListId")]
    public required string TestListId { get; init; }

    [XmlAttribute("executionId")]
    public required string ExecutionId { get; init; }

    [XmlAttribute("testName")]
    public required string TestName { get; init; }

    [XmlAttribute("duration")]
    public required string Duration { get; init; }

    [XmlAttribute("outcome")]
    public required TrxUnitTestResultOutcome Outcome { get; init; }

    [XmlAttribute("startTime")]
    public required DateTimeOffset StartTime { get; init; }

    [XmlAttribute("endTime")]
    public required DateTimeOffset EndTime { get; init; }

    [XmlIgnore]
    public TimeSpan DurationTimeSpan =>
        TimeSpan.TryParse(Duration, out var ts) ? ts : TimeSpan.Zero;
}

public enum TrxUnitTestResultOutcome
{
    Other = default,

    Passed,

    Failed,

    Skipped,

    Pending
}

public record TrxTestLists
{
    [XmlElement("TestList")]
    public required TrxTestList[] Items { get; init; }
}

public record TrxTestList
{
    [XmlAttribute("id")]
    public required string Id { get; init; }

    [XmlAttribute("name")]
    public required string Name { get; init; }
}

public record TrxTimes
{
    [XmlAttribute("creation")]
    public required DateTimeOffset Creation { get; init; }

    [XmlAttribute("start")]
    public required DateTimeOffset Start { get; init; }

    [XmlAttribute("finish")]
    public required DateTimeOffset Finish { get; init; }
}

public record TrxResultSummary
{
    [XmlElement("Counters")]
    public required TrxCounters Counters { get; init; }
}

public record TrxCounters
{
    [XmlAttribute("total")]
    public required int Total { get; init; }

    [XmlAttribute("passed")]
    public required int Passed { get; init; }

    [XmlAttribute("failed")]
    public required int Failed { get; init; }

    [XmlAttribute("pending")]
    public required int Pending { get; init; }

    [XmlAttribute("skipped")]
    public required int Skipped { get; init; }
}

[JsonSourceGenerationOptions(
    defaults: JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    IndentSize = 2,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TrxTestRun))]
[JsonSerializable(typeof(CtrfReport))]
[JsonSerializable(typeof(PerformanceProfile))]
internal partial class StaticJsonSerializerContext : JsonSerializerContext;