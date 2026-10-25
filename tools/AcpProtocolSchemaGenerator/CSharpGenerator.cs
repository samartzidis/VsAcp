using System.Text;
using System.Text.Json;

namespace AcpProtocolSchemaGenerator;

public class CSharpGenerator
{
    const string Namespace = "AcpProtocol";

    readonly JsonElement _defs;
    readonly Dictionary<string, TypeInfo> _typeMap = new();
    readonly Dictionary<string, string> _childToParentUnion = new();

    public int FilesWritten { get; private set; }

    static readonly HashSet<string> InternalOnlyTypes =
    [
        "AgentRequest", "AgentResponse", "AgentNotification",
        "ClientRequest", "ClientResponse", "ClientNotification",
        "Error", "ErrorCode", "ExtRequest", "ExtResponse", "ExtNotification",
        "RequestId"
    ];

    public CSharpGenerator(JsonElement defs)
    {
        _defs = defs;
        AnalyzeTypes();
    }

    void AnalyzeTypes()
    {
        foreach (var def in _defs.EnumerateObject())
        {
            if (InternalOnlyTypes.Contains(def.Name))
                continue;

            if (def.Value.TryGetProperty("x-docs-ignore", out var ignore) && ignore.GetBoolean())
                continue;

            var info = ClassifyType(def.Name, def.Value);
            _typeMap[def.Name] = info;
        }
    }

    TypeInfo ClassifyType(string name, JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out _))
            return new TypeInfo(name, TypeKind.StringEnum, schema);

        if (HasBaseWithVariants(schema))
            return new TypeInfo(name, TypeKind.Record, schema);

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            if (AllStringConsts(oneOf))
                return new TypeInfo(name, TypeKind.StringEnum, schema);

            if (schema.TryGetProperty("discriminator", out _))
                return new TypeInfo(name, TypeKind.DiscriminatedUnion, schema);

            return new TypeInfo(name, TypeKind.TaglessUnion, schema);
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            if (AllStringConsts(anyOf))
                return new TypeInfo(name, TypeKind.StringEnum, schema);

            if (IsStringTypeAlias(anyOf))
                return new TypeInfo(name, TypeKind.TypeAlias, schema);

            if (AllRefsOrStringConsts(anyOf))
                return new TypeInfo(name, TypeKind.TaglessUnion, schema);

            return new TypeInfo(name, TypeKind.TaglessUnion, schema);
        }

        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type))
        {
            var typeStr = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            if (typeStr == "string" || typeStr == "integer")
                return new TypeInfo(name, TypeKind.TypeAlias, schema);

            if (typeStr == "object")
                return new TypeInfo(name, TypeKind.Record, schema);
        }

        if (schema.TryGetProperty("properties", out _))
            return new TypeInfo(name, TypeKind.Record, schema);

        return new TypeInfo(name, TypeKind.Record, schema);
    }

    // An object with its own properties plus untagged anyOf/oneOf variants, e.g. SetSessionConfigOptionRequest
    // or CreateElicitationRequest. The variants cannot be expressed as a C# hierarchy without a discriminator,
    // so the type is modelled as one record with the variants' properties merged (see FlattenVariants).
    static bool HasBaseWithVariants(JsonElement schema)
    {
        if (schema.TryGetProperty("discriminator", out _) || !schema.TryGetProperty("properties", out _))
            return false;

        if (!schema.TryGetProperty("anyOf", out var arr) && !schema.TryGetProperty("oneOf", out arr))
            return false;

        return arr.GetArrayLength() > 0;
    }

    static bool AllStringConsts(JsonElement arr)
    {
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("const", out _))
                continue;
            if (item.TryGetProperty("type", out var t) && t.GetString() == "string"
                && !item.TryGetProperty("const", out _) && !item.TryGetProperty("$ref", out _))
                continue;
            return false;
        }
        return true;
    }

    static bool AllRefsOrStringConsts(JsonElement arr)
    {
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("$ref", out _)) continue;
            if (item.TryGetProperty("allOf", out _)) continue;
            if (item.TryGetProperty("const", out _)) continue;
            if (item.TryGetProperty("type", out var t) && (t.GetString() == "null" || t.GetString() == "string")) continue;
            return false;
        }
        return true;
    }

    static bool IsStringTypeAlias(JsonElement anyOf)
    {
        foreach (var item in anyOf.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t))
            {
                var ts = t.GetString();
                if (ts == "null") continue;
                if (ts == "string" || ts == "integer") return true;
            }
            if (item.TryGetProperty("$ref", out _)) return false;
        }
        return false;
    }

    public void Generate(string outputDir)
    {
        ReclassifyEmptyUnions();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated from the official ACP schema.json");
        sb.AppendLine("// Do not edit manually - run the AcpProtocolSchemaGenerator tool instead.");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {Namespace};");
        sb.AppendLine();

        foreach (var (name, info) in _typeMap.OrderBy(x => x.Key))
        {
            switch (info.Kind)
            {
                case TypeKind.StringEnum:
                    GenerateStringEnum(sb, info);
                    break;
                case TypeKind.Record:
                    GenerateRecord(sb, info);
                    break;
                case TypeKind.DiscriminatedUnion:
                    GenerateDiscriminatedUnion(sb, info);
                    break;
                case TypeKind.TaglessUnion:
                    GenerateTaglessUnion(sb, info);
                    break;
                case TypeKind.TypeAlias:
                    break;
            }
        }

        GenerateSerializerContext(sb);

        var path = Path.Combine(outputDir, "AcpTypes.g.cs");
        File.WriteAllText(path, sb.ToString());
        FilesWritten = 1;
    }

    void ReclassifyEmptyUnions()
    {
        foreach (var (name, info) in _typeMap.ToList())
        {
            if (info.Kind != TypeKind.TaglessUnion) continue;

            JsonElement arr;
            if (info.Schema.TryGetProperty("anyOf", out var anyOf))
                arr = anyOf;
            else if (info.Schema.TryGetProperty("oneOf", out var oneOf))
                arr = oneOf;
            else continue;

            var refs = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                var refName = ExtractRefName(item);
                if (refName != null && !InternalOnlyTypes.Contains(refName))
                    refs.Add(refName);
            }

            if (refs.Count == 0)
            {
                _typeMap[name] = new TypeInfo(name, TypeKind.TypeAlias, info.Schema);
            }
            else
            {
                foreach (var child in refs)
                    _childToParentUnion[child] = name;
            }
        }
    }

    void GenerateStringEnum(StringBuilder sb, TypeInfo info)
    {
        var values = CollectEnumValues(info.Schema);
        if (values.Count == 0) return;

        sb.AppendLine($"[JsonConverter(typeof(JsonStringEnumConverter<{info.Name}>))]");
        sb.AppendLine($"public enum {info.Name}");
        sb.AppendLine("{");

        for (int i = 0; i < values.Count; i++)
        {
            var (jsonVal, csharpName) = values[i];
            sb.AppendLine($"    [JsonStringEnumMemberName(\"{jsonVal}\")]");
            sb.Append($"    {csharpName}");
            sb.AppendLine(i < values.Count - 1 ? "," : "");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    List<(string jsonValue, string csharpName)> CollectEnumValues(JsonElement schema)
    {
        var result = new List<(string, string)>();
        JsonElement arr;

        if (schema.TryGetProperty("enum", out var enumArr))
            arr = enumArr;
        else if (schema.TryGetProperty("oneOf", out var oneOf))
            arr = oneOf;
        else if (schema.TryGetProperty("anyOf", out var anyOf))
            arr = anyOf;
        else
            return result;

        if (schema.TryGetProperty("enum", out _))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var val = item.GetString()!;
                result.Add((val, ToPascalCase(val)));
            }
        }
        else
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("const", out var constVal))
                {
                    var val = constVal.ValueKind == JsonValueKind.Number
                        ? constVal.GetInt32().ToString()
                        : constVal.GetString()!;
                    result.Add((val, ToPascalCase(val)));
                }
                else if (item.TryGetProperty("type", out var t) && t.GetString() == "string"
                    && !item.TryGetProperty("const", out _))
                {
                    // Catch-all "other" variant - skip, can't represent in enum
                }
            }
        }

        return result;
    }

    void GenerateRecord(StringBuilder sb, TypeInfo info)
    {
        var props = GetProperties(info.Schema);
        var required = GetRequired(info.Schema);

        var optionalMerged = new HashSet<string>();
        if (HasBaseWithVariants(info.Schema))
            FlattenVariants(info.Schema, props, required, optionalMerged);

        var baseType = _childToParentUnion.TryGetValue(info.Name, out var parent) ? $" : {parent}" : "";
        sb.AppendLine($"public record {info.Name}{baseType}");
        sb.AppendLine("{");

        foreach (var prop in props)
        {
            var jsonName = prop.Key;
            if (jsonName == "_meta")
            {
                sb.AppendLine($"    [JsonPropertyName(\"_meta\")]");
                sb.AppendLine($"    public JsonElement? Meta {{ get; init; }}");
                sb.AppendLine();
                continue;
            }

            var csType = ResolvePropertyType(prop.Value, required.Contains(jsonName));
            var defaultVal = GetDefaultValue(prop.Value, csType);
            if (optionalMerged.Contains(jsonName) && !csType.EndsWith("?"))
                csType += "?";
            var csName = ToPascalCase(jsonName);
            if (csName == info.Name)
                csName += "Value";

            sb.AppendLine($"    [JsonPropertyName(\"{jsonName}\")]");

            if (defaultVal != null)
                sb.AppendLine($"    public {csType} {csName} {{ get; init; }} = {defaultVal};");
            else if (required.Contains(jsonName) && !csType.EndsWith("?"))
                sb.AppendLine($"    public required {csType} {csName} {{ get; init; }}");
            else
                sb.AppendLine($"    public {csType} {csName} {{ get; init; }}");

            sb.AppendLine();
        }

        sb.AppendLine($"    [JsonExtensionData]");
        sb.AppendLine($"    public Dictionary<string, JsonElement>? ExtensionData {{ get; set; }}");

        sb.AppendLine("}");
        sb.AppendLine();
    }

    void GenerateDiscriminatedUnion(StringBuilder sb, TypeInfo info)
    {
        var discriminatorProp = info.Schema.GetProperty("discriminator").GetProperty("propertyName").GetString()!;
        var variants = info.Schema.GetProperty("oneOf");
        var variantInfos = new List<(string constVal, string typeName, JsonElement schema)>();

        foreach (var variant in variants.EnumerateArray())
        {
            string? constVal = null;
            if (variant.TryGetProperty("properties", out var vProps)
                && vProps.TryGetProperty(discriminatorProp, out var discProp)
                && discProp.TryGetProperty("const", out var c))
            {
                constVal = c.GetString();
            }

            string? refType = null;
            if (variant.TryGetProperty("allOf", out var allOf))
            {
                foreach (var a in allOf.EnumerateArray())
                {
                    if (a.TryGetProperty("$ref", out var r))
                    {
                        refType = r.GetString()!.Split('/').Last();
                        break;
                    }
                }
            }

            if (constVal != null)
            {
                var typeName = refType ?? (info.Name + ToPascalCase(constVal));
                variantInfos.Add((constVal, typeName, variant));
            }
        }

        var csPropName = ToPascalCase(discriminatorProp);
        if (csPropName == info.Name)
            csPropName += "Kind";

        sb.AppendLine($"[JsonConverter(typeof({info.Name}JsonConverter))]");
        sb.AppendLine($"public abstract record {info.Name}");
        sb.AppendLine("{");
        sb.AppendLine($"    [JsonPropertyName(\"{discriminatorProp}\")]");
        sb.AppendLine($"    public abstract string {csPropName} {{ get; }}");
        sb.AppendLine();

        var baseProps = GetProperties(info.Schema);
        foreach (var prop in baseProps)
        {
            if (prop.Key == "_meta" || prop.Key == discriminatorProp) continue;

            var csType = ResolvePropertyType(prop.Value, false);
            var propName = ToPascalCase(prop.Key);
            if (propName == info.Name) propName += "Value";

            sb.AppendLine($"    [JsonPropertyName(\"{prop.Key}\")]");
            if (csType.EndsWith("?"))
                sb.AppendLine($"    public {csType} {propName} {{ get; init; }}");
            else
                sb.AppendLine($"    public {csType} {propName} {{ get; init; }} = default!;");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var (constVal, typeName, variantSchema) in variantInfos)
        {
            var refDef = ResolveRefDef(variantSchema);
            var csVariantName = info.Name + ToPascalCase(constVal);

            var props = GetProperties(refDef ?? variantSchema);
            var required = refDef != null ? GetRequired(refDef.Value) : GetRequired(variantSchema);

            sb.AppendLine($"public record {csVariantName} : {info.Name}");
            sb.AppendLine("{");
            sb.AppendLine($"    [JsonPropertyName(\"{discriminatorProp}\")]");
            sb.AppendLine($"    public override string {csPropName} => \"{constVal}\";");
            sb.AppendLine();

            foreach (var prop in props)
            {
                if (prop.Key == discriminatorProp) continue;
                // Agents put their own data here (claude-agent-acp: the files a shell command changed).
                if (prop.Key == "_meta")
                {
                    sb.AppendLine($"    [JsonPropertyName(\"_meta\")]");
                    sb.AppendLine($"    public JsonElement? Meta {{ get; init; }}");
                    sb.AppendLine();
                    continue;
                }

                var csType = ResolvePropertyType(prop.Value, required.Contains(prop.Key));
                var propName = ToPascalCase(prop.Key);

                sb.AppendLine($"    [JsonPropertyName(\"{prop.Key}\")]");
                if (required.Contains(prop.Key) && !csType.EndsWith("?"))
                    sb.AppendLine($"    public required {csType} {propName} {{ get; init; }}");
                else
                    sb.AppendLine($"    public {csType} {propName} {{ get; init; }}");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine($"public record {info.Name}Unknown : {info.Name}");
        sb.AppendLine("{");
        sb.AppendLine($"    [JsonPropertyName(\"{discriminatorProp}\")]");
        sb.AppendLine($"    public override string {csPropName} => RawType;");
        sb.AppendLine();
        sb.AppendLine($"    public string RawType {{ get; set; }} = \"unknown\";");
        sb.AppendLine("}");
        sb.AppendLine();

        GeneratePolymorphicConverter(sb, info.Name, discriminatorProp, csPropName, variantInfos);
    }

    void GenerateTaglessUnion(StringBuilder sb, TypeInfo info)
    {
        JsonElement arr;
        if (info.Schema.TryGetProperty("anyOf", out var anyOf))
            arr = anyOf;
        else if (info.Schema.TryGetProperty("oneOf", out var oneOf))
            arr = oneOf;
        else return;

        var refs = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var refName = ExtractRefName(item);
            if (refName != null && !InternalOnlyTypes.Contains(refName))
                refs.Add(refName);
        }

        if (refs.Count == 0) return;

        sb.AppendLine($"[JsonConverter(typeof({info.Name}JsonConverter))]");
        sb.AppendLine($"public abstract record {info.Name};");
        sb.AppendLine();

        GenerateTaglessConverter(sb, info.Name, refs, TryGetConstDiscriminator(arr));
    }

    /// <summary>
    /// Detects variants shaped <c>{ properties: { &lt;disc&gt;: { const } }, allOf: [$ref] }</c> that all
    /// share one discriminator property (e.g. ElicitationPropertySchema's <c>type</c>). Returns null
    /// when the variants are not uniformly tagged that way.
    /// </summary>
    (string discProp, List<(string constVal, string refName)> variants)? TryGetConstDiscriminator(JsonElement arr)
    {
        string? discProp = null;
        var variants = new List<(string, string)>();

        foreach (var item in arr.EnumerateArray())
        {
            var refName = ExtractRefName(item);
            if (refName == null)
                continue; // catch-all "other" variant: no concrete record to map to

            if (!item.TryGetProperty("properties", out var props))
                return null;

            string? prop = null, constVal = null;
            foreach (var p in props.EnumerateObject())
            {
                if (p.Value.TryGetProperty("const", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    prop = p.Name;
                    constVal = c.GetString();
                }
            }

            if (prop == null || (discProp != null && discProp != prop))
                return null;

            discProp = prop;
            if (!InternalOnlyTypes.Contains(refName))
                variants.Add((constVal!, refName));
        }

        return discProp == null || variants.Count == 0 ? null : (discProp, variants);
    }

    void GeneratePolymorphicConverter(StringBuilder sb, string baseName, string discProp, string csPropName,
        List<(string constVal, string typeName, JsonElement schema)> variants)
    {
        sb.AppendLine($"public class {baseName}JsonConverter : JsonConverter<{baseName}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public override {baseName}? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        using var doc = JsonDocument.ParseValue(ref reader);");
        sb.AppendLine("        var root = doc.RootElement;");
        sb.AppendLine();
        sb.AppendLine($"        if (!root.TryGetProperty(\"{discProp}\", out var disc))");
        sb.AppendLine($"            throw new JsonException(\"Missing '{discProp}' in {baseName}\");");
        sb.AppendLine();
        sb.AppendLine("        var type = disc.GetString();");
        sb.AppendLine("        return type switch");
        sb.AppendLine("        {");

        foreach (var (constVal, _, _) in variants)
        {
            var csVariant = baseName + ToPascalCase(constVal);
            sb.AppendLine($"            \"{constVal}\" => root.Deserialize<{csVariant}>(options),");
        }

        sb.AppendLine("            _ => Deserialize(root, type, options)");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    static {baseName}Unknown Deserialize(JsonElement root, string? type, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var unknown = root.Deserialize<{baseName}Unknown>(options) ?? new {baseName}Unknown();");
        sb.AppendLine($"        unknown.RawType = type ?? \"null\";");
        sb.AppendLine("        return unknown;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public override void Write(Utf8JsonWriter writer, {baseName} value, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        JsonSerializer.Serialize(writer, value, value.GetType(), options);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    void GenerateTaglessConverter(StringBuilder sb, string baseName, List<string> refs,
        (string discProp, List<(string constVal, string refName)> variants)? constDiscriminator = null)
    {
        sb.AppendLine($"public class {baseName}JsonConverter : JsonConverter<{baseName}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public override {baseName}? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        using var doc = JsonDocument.ParseValue(ref reader);");
        sb.AppendLine("        var root = doc.RootElement;");
        sb.AppendLine();

        // Uniformly tagged variants: switch on the shared const property. Probing for a unique
        // property would misfire here because the variants have the same property names.
        if (constDiscriminator is { } disc)
        {
            sb.AppendLine($"        if (root.TryGetProperty(\"{disc.discProp}\", out var discValue) && discValue.ValueKind == JsonValueKind.String)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (discValue.GetString())");
            sb.AppendLine("            {");
            foreach (var (constVal, refName) in disc.variants)
                sb.AppendLine($"                case \"{constVal}\": return root.Deserialize<{refName}>(options);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        throw new JsonException($\"Unable to determine {baseName} variant from '{disc.discProp}'\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public override void Write(Utf8JsonWriter writer, {baseName} value, JsonSerializerOptions options)");
            sb.AppendLine("    {");
            sb.AppendLine("        JsonSerializer.Serialize(writer, value, value.GetType(), options);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            return;
        }

        foreach (var refName in refs)
        {
            var discKey = FindDiscriminatingProperty(refName);
            if (discKey != null)
            {
                sb.AppendLine($"        if (root.TryGetProperty(\"{discKey}\", out _))");
                sb.AppendLine($"            return root.Deserialize<{refName}>(options);");
                sb.AppendLine();
            }
        }

        sb.AppendLine($"        throw new JsonException(\"Unable to determine {baseName} variant\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public override void Write(Utf8JsonWriter writer, {baseName} value, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        JsonSerializer.Serialize(writer, value, value.GetType(), options);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    string? FindDiscriminatingProperty(string typeName)
    {
        if (!_defs.TryGetProperty(typeName, out var def)) return null;
        var props = GetProperties(def);
        foreach (var p in props)
        {
            if (p.Key != "_meta" && p.Key != "uri" && p.Key != "mimeType")
                return p.Key;
        }
        return null;
    }

    void GenerateSerializerContext(StringBuilder sb)
    {
        sb.AppendLine("[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]");

        foreach (var (name, info) in _typeMap.OrderBy(x => x.Key))
        {
            if (info.Kind == TypeKind.TypeAlias) continue;

            sb.AppendLine($"[JsonSerializable(typeof({name}))]");

            if (info.Kind == TypeKind.DiscriminatedUnion)
            {
                var variants = info.Schema.GetProperty("oneOf");
                foreach (var variant in variants.EnumerateArray())
                {
                    if (variant.TryGetProperty("properties", out var vProps))
                    {
                        var discProp = info.Schema.GetProperty("discriminator").GetProperty("propertyName").GetString()!;
                        if (vProps.TryGetProperty(discProp, out var dp) && dp.TryGetProperty("const", out var c))
                        {
                            var csVariant = name + ToPascalCase(c.GetString()!);
                            sb.AppendLine($"[JsonSerializable(typeof({csVariant}))]");
                        }
                    }
                }
                sb.AppendLine($"[JsonSerializable(typeof({name}Unknown))]");
            }
        }

        sb.AppendLine("[JsonSerializable(typeof(RequestId))]");
        sb.AppendLine("[JsonSerializable(typeof(JsonRpcMessage))]");
        sb.AppendLine("[JsonSerializable(typeof(JsonRpcRequest))]");
        sb.AppendLine("[JsonSerializable(typeof(JsonRpcResponse))]");
        sb.AppendLine("[JsonSerializable(typeof(JsonRpcNotification))]");
        sb.AppendLine("[JsonSerializable(typeof(JsonRpcError))]");
        sb.AppendLine("public partial class AcpJsonSerializerContext : JsonSerializerContext;");
    }

    JsonElement? ResolveRefDef(JsonElement schema)
    {
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var a in allOf.EnumerateArray())
            {
                if (a.TryGetProperty("$ref", out var r))
                {
                    var refName = r.GetString()!.Split('/').Last();
                    if (_defs.TryGetProperty(refName, out var def))
                        return def;
                }
            }
        }
        return null;
    }

    string? ExtractRefName(JsonElement item)
    {
        if (item.TryGetProperty("$ref", out var r))
            return r.GetString()!.Split('/').Last();
        if (item.TryGetProperty("allOf", out var allOf))
        {
            foreach (var a in allOf.EnumerateArray())
            {
                if (a.TryGetProperty("$ref", out var rr))
                    return rr.GetString()!.Split('/').Last();
            }
        }
        return null;
    }

    Dictionary<string, JsonElement> GetProperties(JsonElement schema)
    {
        var result = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
                result[p.Name] = p.Value;
        }
        return result;
    }

    // Adds each variant's properties to the record, following `allOf: [$ref]` into referenced definitions
    // (which may themselves be base-with-variants). A property required in every variant stays required;
    // one that appears in several variants with different shapes becomes a string or a raw JsonElement.
    void FlattenVariants(JsonElement schema, Dictionary<string, JsonElement> props, HashSet<string> required,
        HashSet<string>? optionalMerged = null)
    {
        var arr = schema.TryGetProperty("anyOf", out var a) ? a : schema.GetProperty("oneOf");
        var variantCount = arr.GetArrayLength();
        var seen = new HashSet<string>();
        var requiredIn = new Dictionary<string, int>();
        var shapes = new Dictionary<string, List<JsonElement>>();

        foreach (var variant in arr.EnumerateArray())
        {
            var vProps = GetProperties(variant);
            var vRequired = GetRequired(variant);

            if (variant.TryGetProperty("allOf", out var allOf))
            {
                foreach (var item in allOf.EnumerateArray())
                {
                    if (!item.TryGetProperty("$ref", out var r) ||
                        !_defs.TryGetProperty(r.GetString()!.Split('/').Last(), out var refDef))
                        continue;

                    var refProps = GetProperties(refDef);
                    var refRequired = GetRequired(refDef);
                    if (HasBaseWithVariants(refDef))
                        FlattenVariants(refDef, refProps, refRequired);

                    foreach (var (n, v) in refProps) vProps.TryAdd(n, v);
                    vRequired.UnionWith(refRequired);
                }
            }

            foreach (var (propName, propSchema) in vProps)
            {
                if (props.ContainsKey(propName) && !seen.Contains(propName))
                    continue; // declared on the record itself

                seen.Add(propName);
                if (vRequired.Contains(propName))
                    requiredIn[propName] = requiredIn.GetValueOrDefault(propName) + 1;

                if (!shapes.TryGetValue(propName, out var list))
                    shapes[propName] = list = [];
                list.Add(propSchema);
            }
        }

        foreach (var propName in seen)
        {
            var variants = shapes[propName];
            var first = variants[0].GetRawText();
            if (variants.Any(v => v.GetRawText() != first))
            {
                var allStrings = variants.All(v => v.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "string");
                props[propName] = JsonDocument.Parse(allStrings ? "{\"type\":\"string\"}" : "{}").RootElement.Clone();
            }
            else
            {
                props[propName] = variants[0];
            }

            if (requiredIn.GetValueOrDefault(propName) == variantCount)
                required.Add(propName);
            else
                optionalMerged?.Add(propName);
        }
    }

    HashSet<string> GetRequired(JsonElement schema)
    {
        var result = new HashSet<string>();
        if (schema.TryGetProperty("required", out var req))
        {
            foreach (var r in req.EnumerateArray())
                result.Add(r.GetString()!);
        }
        return result;
    }

    string ResolvePropertyType(JsonElement prop, bool isRequired)
    {
        if (prop.TryGetProperty("allOf", out var allOf))
        {
            foreach (var a in allOf.EnumerateArray())
            {
                if (a.TryGetProperty("$ref", out var r))
                    return MapRefType(r.GetString()!.Split('/').Last());
            }
        }

        if (prop.TryGetProperty("$ref", out var directRef))
            return MapRefType(directRef.GetString()!.Split('/').Last());

        if (prop.TryGetProperty("anyOf", out var anyOf))
        {
            string? refType = null;
            bool hasNull = false;
            foreach (var item in anyOf.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var nt))
                {
                    var ts = nt.ValueKind == JsonValueKind.String ? nt.GetString() : null;
                    if (ts == "null") { hasNull = true; continue; }
                    if (ts == "array" && item.TryGetProperty("items", out var arrItems))
                    {
                        var elemType = ResolvePropertyType(arrItems, true);
                        refType = elemType + "[]";
                        continue;
                    }
                }
                var rn = ExtractRefName(item);
                if (rn != null) refType = MapRefType(rn);
            }
            if (refType != null)
                return hasNull ? refType + "?" : refType;
        }

        if (prop.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.Array)
            {
                var types = new List<string>();
                bool nullable = false;
                foreach (var t in type.EnumerateArray())
                {
                    var ts = t.GetString()!;
                    if (ts == "null") nullable = true;
                    else types.Add(ts);
                }
                if (types.Count == 1)
                {
                    if (types[0] == "array" && prop.TryGetProperty("items", out var arrItems))
                    {
                        var elemType = ResolvePropertyType(arrItems, true);
                        return nullable ? elemType + "[]?" : elemType + "[]";
                    }
                    var baseType = MapJsonType(types[0], prop);
                    return nullable ? baseType + "?" : baseType;
                }
            }
            else
            {
                var typeStr = type.GetString()!;
                if (typeStr == "array")
                {
                    if (prop.TryGetProperty("items", out var items))
                    {
                        var itemType = ResolvePropertyType(items, true);
                        // An optional array is absent (null) unless the agent sends it; nullable keeps
                        // that honest and keeps it off the wire when unset (WhenWritingDefault).
                        return isRequired ? itemType + "[]" : itemType + "[]?";
                    }
                    return isRequired ? "JsonElement[]" : "JsonElement[]?";
                }
                return MapJsonType(typeStr, prop);
            }
        }

        return isRequired ? "JsonElement" : "JsonElement?";
    }

    static string MapJsonType(string jsonType, JsonElement prop)
    {
        return jsonType switch
        {
            "string" => "string",
            "boolean" => "bool",
            "integer" => prop.TryGetProperty("format", out var f)
                ? f.GetString() switch
                {
                    "int64" => "long",
                    "uint64" => "ulong",
                    "uint32" => "uint",
                    "uint16" => "ushort",
                    "int32" => "int",
                    _ => "int"
                }
                : "int",
            "number" => "double",
            "object" => "JsonElement",
            _ => "JsonElement"
        };
    }

    string MapRefType(string refName)
    {
        if (IsTypeAlias(refName))
            return ResolveAliasType(refName);
        return refName;
    }

    bool IsTypeAlias(string refName)
    {
        return _typeMap.TryGetValue(refName, out var info) && info.Kind == TypeKind.TypeAlias;
    }

    string ResolveAliasType(string refName)
    {
        if (!_defs.TryGetProperty(refName, out var def)) return "string";

        if (def.TryGetProperty("type", out var type))
        {
            var ts = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            return ts switch
            {
                "string" => "string",
                "integer" => def.TryGetProperty("format", out var f)
                    ? f.GetString() switch
                    {
                        "uint16" => "ushort",
                        "int32" => "int",
                        "int64" => "long",
                        _ => "int"
                    }
                    : "int",
                _ => "JsonElement"
            };
        }

        if (def.TryGetProperty("anyOf", out _) || def.TryGetProperty("oneOf", out _))
            return "JsonElement";

        return "string";
    }

    string? GetDefaultValue(JsonElement prop, string csType)
    {
        if (!prop.TryGetProperty("default", out var def)) return null;

        return def.ValueKind switch
        {
            JsonValueKind.False => "false",
            JsonValueKind.True => "true",
            JsonValueKind.Number => def.ToString(),
            JsonValueKind.String when _typeMap.TryGetValue(csType, out var enumInfo) && enumInfo.Kind == TypeKind.StringEnum
                => $"{csType}.{ToPascalCase(def.GetString()!)}",
            JsonValueKind.String => $"\"{def.GetString()}\"",
            JsonValueKind.Array when def.GetArrayLength() == 0 => "[]",
            JsonValueKind.Object => $"new {csType}()",
            _ => null
        };
    }

    static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder();
        bool capitalizeNext = true;

        foreach (var ch in input)
        {
            if (ch == '_' || ch == '-' || ch == ' ')
            {
                capitalizeNext = true;
                continue;
            }

            sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
            capitalizeNext = false;
        }

        return sb.ToString();
    }
}

enum TypeKind
{
    Record,
    StringEnum,
    DiscriminatedUnion,
    TaglessUnion,
    TypeAlias
}

record TypeInfo(string Name, TypeKind Kind, JsonElement Schema);
