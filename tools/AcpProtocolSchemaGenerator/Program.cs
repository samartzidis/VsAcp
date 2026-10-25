using System.Text.Json;
using AcpProtocolSchemaGenerator;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AcpProtocolSchemaGenerator <schema.json> <output-dir>");
    return 1;
}

var schemaPath = args[0];
var outputDir = args[1];

if (!File.Exists(schemaPath))
{
    Console.Error.WriteLine($"Schema file not found: {schemaPath}");
    return 1;
}

Directory.CreateDirectory(outputDir);

var json = File.ReadAllText(schemaPath);
var doc = JsonDocument.Parse(json);
var defs = doc.RootElement.GetProperty("$defs");

var generator = new CSharpGenerator(defs);
generator.Generate(outputDir);

Console.WriteLine($"Generated {generator.FilesWritten} files in {outputDir}");
return 0;
