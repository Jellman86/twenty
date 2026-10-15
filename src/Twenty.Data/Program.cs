using System.Text.Json;
using Twenty.Core.Data;

if (args.Length is not (2 or 4) || args[0] != "validate" || (args.Length == 4 && args[2] != "--archive"))
{
    Console.Error.WriteLine("Usage: Twenty.Data validate <dataset.json> [--archive <directory>]");
    return 2;
}
try
{
    var bytes = DatasetArchive.ReadBounded(args[1]);
    var file = DatasetJson.Read(bytes);
    var validation = DatasetValidator.Validate(file);
    var archive = validation.IsValid && args.Length == 4 ? DatasetArchive.Store(args[3], bytes) : null;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid = validation.IsValid,
        schemaVersion = file.Manifest.SchemaVersion,
        datasetId = validation.IsValid ? file.Manifest.DatasetId : null,
        synthetic = file.Manifest.Synthetic,
        observations = file.Observations.Length,
        issues = validation.Issues,
        archive
    }, DatasetJson.Options));
    return validation.IsValid ? 0 : 1;
}
catch (JsonException error)
{
    // Do not echo raw input or framework exception messages into logs.
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = "json-shape", line = error.LineNumber, position = error.BytePositionInLine }));
    return 1;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDatasetException)
{
    Console.Error.WriteLine("{\"valid\":false,\"code\":\"input-or-archive\"}");
    return 1;
}
