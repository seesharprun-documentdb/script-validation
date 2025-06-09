# Validate MongoDB commands/operators

## Tool

Derived from https://github.com/ctrf-io/dotnet-ctrf-json-reporter

## Validation

.NET script

## Steps

```bash
docker run --detach --tty --publish 10260:10260 --env USERNAME="<username>" --env PASSWORD="<password>" ghcr.io/microsoft/documentdb/documentdb-local:latest
```

```bash
cd utilties
```

```bash
DOCUMENTDB_IDENTITY="<username>"
DOCUMENTDB_CREDENTIAL="<password>"
```

```bash
dotnet run validate.cs --results-directory "tst" --report-trx --report-trx-filename "results.trx" --no-ansi --ignore-exit-code "2"
```

```bash
dotnet tool run DotnetCtrfJsonReporter --test-tool "mstest" --trx-path "tst/results.trx" --output-directory "ctrf" --output-filename "report.json"
```

- <https://devblogs.microsoft.com/cosmosdb/documentdb-local-mongodb-api-on-your-machine>
- <https://github.com/microsoft/documentdb>
