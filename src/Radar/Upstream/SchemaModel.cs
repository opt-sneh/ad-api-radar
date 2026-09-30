using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Radar;

public sealed record SchemaField(
    string Path,
    string ProtoType,
    bool Repeated,
    bool Deprecated,
    string ProtoFile,
    int? Line
)
{
    public string? Location => Line is null ? null : $"{ProtoFile}:{Line}";
}

public sealed record SchemaEntry(string Path, bool Deprecated, string ProtoFile, int? Line)
{
    public string? Location => Line is null ? null : $"{ProtoFile}:{Line}";
    public string? CsharpName { get; init; }
}

public sealed record ProtoField(
    string Path, string ProtoType, bool Repeated, bool Deprecated, bool Required,
    string ProtoFile, int? Line, string CsharpName)
{
    public string? Location => Line is null ? null : $"{ProtoFile}:{Line}";
}

public sealed record SchemaMessage(string Path, bool IsResource, string? GaqlName,
    string ProtoFile, int? Line)
{
    public string? Location => Line is null ? null : $"{ProtoFile}:{Line}";
}

public sealed class SchemaModel
{
    private const int ResourceOptionNumber = 1053;
    private const int MaximumFieldDepth = 6;

    public string Version { get; }
    public IReadOnlyDictionary<string, SchemaField> Fields { get; }
    public IReadOnlyDictionary<string, ProtoField> ProtoFields { get; }
    public IReadOnlyDictionary<string, SchemaMessage> Messages { get; }
    public IReadOnlyDictionary<string, SchemaEntry> Enums { get; }
    public IReadOnlyDictionary<string, SchemaEntry> Services { get; }
    public IReadOnlyDictionary<string, SchemaEntry> ServiceNames { get; }

    private SchemaModel(
        string version,
        Dictionary<string, SchemaField> fields,
        Dictionary<string, ProtoField> protoFields,
        Dictionary<string, SchemaMessage> schemaMessages,
        Dictionary<string, SchemaEntry> enums,
        Dictionary<string, SchemaEntry> services,
        Dictionary<string, SchemaEntry> serviceNames
    )
    {
        Version = version;
        Fields = fields;
        ProtoFields = protoFields;
        Messages = schemaMessages;
        Enums = enums;
        Services = services;
        ServiceNames = serviceNames;
    }

    public static SchemaModel Load(string pbPath, string version)
    {
        if (!IsVersion(version))
            throw new ArgumentException("Version must be v followed by digits.", nameof(version));
        var descriptor = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(pbPath));
        string prefix = $"google.ads.googleads.{version}";
        string resourcesPackage = prefix + ".resources";
        string commonPackage = prefix + ".common";
        string enumsPackage = prefix + ".enums";
        string servicesPackage = prefix + ".services";
        string errorsPackage = prefix + ".errors";
        var messages = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        var origins = new Dictionary<string, MessageOrigin>(StringComparer.Ordinal);
        foreach (var file in descriptor.File)
        {
            var lines = SourceLines(file);
            for (int i = 0; i < file.MessageType.Count; i++)
                IndexMessage(
                    messages,
                    origins,
                    file,
                    lines,
                    file.Package,
                    file.MessageType[i],
                    $"4.{i}"
                );
        }

        var fields = new Dictionary<string, SchemaField>(StringComparer.Ordinal);
        var protoFields = new Dictionary<string, ProtoField>(StringComparer.Ordinal);
        var schemaMessages = new Dictionary<string, SchemaMessage>(StringComparer.Ordinal);
        var enums = new Dictionary<string, SchemaEntry>(StringComparer.Ordinal);
        var services = new Dictionary<string, SchemaEntry>(StringComparer.Ordinal);
        var serviceNames = new Dictionary<string, SchemaEntry>(StringComparer.Ordinal);
        foreach (var file in descriptor.File)
        {
            var lines = SourceLines(file);
            if (file.Package == resourcesPackage || file.Package == commonPackage
                || file.Package == servicesPackage || file.Package == enumsPackage
                || file.Package == errorsPackage)
                for (int i = 0; i < file.MessageType.Count; i++)
                    AddProtoMessage(protoFields, schemaMessages, file.MessageType[i], file,
                        lines, $"4.{i}", file.Package[(prefix.Length + 1)..], version);
            if (file.Package == resourcesPackage || file.Package == commonPackage)
            {
                for (int i = 0; i < file.MessageType.Count; i++)
                {
                    var message = file.MessageType[i];
                    string? root =
                        file.Package == resourcesPackage && IsResource(message)
                            ? SnakeCase(message.Name)
                        : file.Package == commonPackage && message.Name == "Metrics" ? "metrics"
                        : file.Package == commonPackage && message.Name == "Segments" ? "segments"
                        : null;
                    if (root is not null)
                        AddFields(
                            fields,
                            messages,
                            origins,
                            version,
                            message,
                            root,
                            file,
                            lines,
                            $"4.{i}",
                            1,
                            new HashSet<string>(StringComparer.Ordinal)
                            {
                                $"{file.Package}.{message.Name}",
                            }
                        );
                }
            }
            if (file.Package == resourcesPackage || file.Package == commonPackage
                || file.Package == servicesPackage || file.Package == enumsPackage
                || file.Package == errorsPackage)
            {
                for (int i = 0; i < file.EnumType.Count; i++)
                    AddEnum(enums, file.EnumType[i], file, lines, $"5.{i}", null);
                for (int i = 0; i < file.MessageType.Count; i++)
                    AddNestedEnums(enums, file.MessageType[i], file, lines, $"4.{i}");
            }
            if (file.Package == servicesPackage)
                for (int i = 0; i < file.Service.Count; i++)
                for (int j = 0; j < file.Service[i].Method.Count; j++)
                {
                    string path = $"{file.Service[i].Name}.{file.Service[i].Method[j].Name}";
                    services.Add(
                        path,
                        new SchemaEntry(path, false, file.Name, Line(lines, $"6.{i}.2.{j}"))
                        { CsharpName = file.Service[i].Name + "Client." + file.Service[i].Method[j].Name }
                    );
                }
            if (file.Package == servicesPackage)
                for (int i = 0; i < file.Service.Count; i++)
                {
                    string name = file.Service[i].Name;
                    serviceNames.TryAdd(name, new SchemaEntry(name, false, file.Name,
                        Line(lines, $"6.{i}")) { CsharpName = name + "Client" });
                }
        }
        if (fields.Count == 0)
            throw new FormatException($"Descriptor set has no fields for {version}.");
        return new SchemaModel(version, fields, protoFields, schemaMessages, enums, services, serviceNames);
    }

    public static bool IsVersion(string version) =>
        version.Length > 1
        && version[0] == 'v'
        && version.AsSpan(1).ToString().All(char.IsAsciiDigit);

    private static void AddProtoMessage(
        Dictionary<string, ProtoField> fields, Dictionary<string, SchemaMessage> messages,
        DescriptorProto message, FileDescriptorProto file,
        Dictionary<string, int?> lines, string sourcePath, string name, string version)
    {
        string path = name + "." + message.Name;
        bool resource = IsResource(message);
        messages.Add(path, new SchemaMessage(path, resource,
            resource ? SnakeCase(message.Name) : null, file.Name, Line(lines, sourcePath)));
        for (int i = 0; i < message.Field.Count; i++)
        {
            var field = message.Field[i];
            string fieldPath = path + "." + field.Name;
            string type = field.Type is FieldDescriptorProto.Types.Type.Message
                or FieldDescriptorProto.Types.Type.Enum
                ? NeutralTypeName(field.TypeName.TrimStart('.'), version)
                : field.Type.ToString().ToLowerInvariant();
            fields.Add(fieldPath, new ProtoField(fieldPath, type,
                field.Label == FieldDescriptorProto.Types.Label.Repeated,
                field.Options?.Deprecated == true, IsRequired(field), file.Name,
                Line(lines, $"{sourcePath}.2.{i}"), PascalCase(field.Name)));
        }
        for (int i = 0; i < message.NestedType.Count; i++)
            AddProtoMessage(fields, messages, message.NestedType[i], file, lines,
                $"{sourcePath}.3.{i}", path, version);
    }

    private static bool IsRequired(FieldDescriptorProto field)
    {
        if (field.Options is null)
            return false;
        using var input = new CodedInputStream(field.Options.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) != 1052)
            {
                input.SkipLastField();
                continue;
            }
            if ((tag & 7) == 0)
            {
                if (input.ReadEnum() == 2)
                    return true;
            }
            else if ((tag & 7) == 2)
            {
                var bytes = input.ReadBytes();
                using var packed = new CodedInputStream(bytes.ToByteArray());
                while (!packed.IsAtEnd)
                    if (packed.ReadEnum() == 2)
                        return true;
            }
            else
                input.SkipLastField();
        }
        return false;
    }

    public static string PascalCase(string value) => string.Concat(
        value.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    private static void IndexMessage(
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, MessageOrigin> origins,
        FileDescriptorProto file,
        Dictionary<string, int?> lines,
        string parent,
        DescriptorProto message,
        string sourcePath
    )
    {
        string name = parent + "." + message.Name;
        messages.Add(name, message);
        origins.Add(name, new MessageOrigin(file, lines, sourcePath));
        for (int i = 0; i < message.NestedType.Count; i++)
            IndexMessage(
                messages,
                origins,
                file,
                lines,
                name,
                message.NestedType[i],
                $"{sourcePath}.3.{i}"
            );
    }

    private static void AddFields(
        Dictionary<string, SchemaField> fields,
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, MessageOrigin> origins,
        string version,
        DescriptorProto message,
        string prefix,
        FileDescriptorProto file,
        Dictionary<string, int?> lines,
        string sourcePath,
        int depth,
        HashSet<string> ancestors
    )
    {
        for (int i = 0; i < message.Field.Count; i++)
        {
            var field = message.Field[i];
            string path = prefix + "." + field.Name;
            string type = field.Type
                is FieldDescriptorProto.Types.Type.Message
                    or FieldDescriptorProto.Types.Type.Enum
                ? NeutralTypeName(field.TypeName.TrimStart('.'), version)
                : field.Type.ToString().ToLowerInvariant();
            fields.TryAdd(
                path,
                new SchemaField(
                    path,
                    type,
                    field.Label == FieldDescriptorProto.Types.Label.Repeated,
                    field.Options?.Deprecated == true,
                    file.Name,
                    Line(lines, $"{sourcePath}.2.{i}")
                )
            );
            if (field.Type != FieldDescriptorProto.Types.Type.Message || depth >= MaximumFieldDepth)
                continue;
            string typeName = field.TypeName.TrimStart('.');
            if (
                !messages.TryGetValue(typeName, out var nested)
                || !ancestors.Add(typeName)
            )
                continue;
            // Source locations for referenced messages live in their defining file. The
            // recursive index below resolves that origin, including cross-file imports.
            if (origins.TryGetValue(typeName, out var origin))
                AddFields(
                    fields,
                    messages,
                    origins,
                    version,
                    nested,
                    path,
                    origin.File,
                    origin.Lines,
                    origin.SourcePath,
                    depth + 1,
                    ancestors
                );
            ancestors.Remove(typeName);
        }
    }

    private sealed record MessageOrigin(
        FileDescriptorProto File,
        Dictionary<string, int?> Lines,
        string SourcePath
    );

    private static string NeutralTypeName(string typeName, string version)
    {
        string versionedPrefix = $"google.ads.googleads.{version}.";
        return typeName.StartsWith(versionedPrefix, StringComparison.Ordinal)
            ? "google.ads.googleads." + typeName[versionedPrefix.Length..]
            : typeName;
    }

    private static Dictionary<string, int?> SourceLines(FileDescriptorProto file)
    {
        var lines = new Dictionary<string, int?>();
        if (file.SourceCodeInfo is not null)
            foreach (var location in file.SourceCodeInfo.Location)
                lines[string.Join(".", location.Path)] =
                    location.Span.Count == 0 ? null : location.Span[0] + 1;
        return lines;
    }

    private static int? Line(Dictionary<string, int?> lines, string path) =>
        lines.TryGetValue(path, out int? line) ? line : null;

    private static bool IsResource(DescriptorProto message)
    {
        if (message.Options is null)
            return false;
        using var input = new CodedInputStream(message.Options.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == ResourceOptionNumber)
                return true;
            input.SkipLastField();
        }
        return false;
    }

    private static string SnakeCase(string value)
    {
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (
                i > 0
                && char.IsUpper(value[i])
                && (
                    char.IsLower(value[i - 1])
                    || char.IsDigit(value[i - 1])
                    || i + 1 < value.Length
                        && char.IsLower(value[i + 1])
                        && char.IsUpper(value[i - 1])
                )
            )
                result.Append('_');
            result.Append(char.ToLowerInvariant(value[i]));
        }
        return result.ToString();
    }

    private static void AddNestedEnums(
        Dictionary<string, SchemaEntry> result,
        DescriptorProto message,
        FileDescriptorProto file,
        Dictionary<string, int?> lines,
        string sourcePath
    )
    {
        for (int i = 0; i < message.EnumType.Count; i++)
            AddEnum(result, message.EnumType[i], file, lines, $"{sourcePath}.4.{i}", message.Name);
        for (int i = 0; i < message.NestedType.Count; i++)
            AddNestedEnums(result, message.NestedType[i], file, lines, $"{sourcePath}.3.{i}");
    }

    private static void AddEnum(
        Dictionary<string, SchemaEntry> result,
        EnumDescriptorProto descriptor,
        FileDescriptorProto file,
        Dictionary<string, int?> lines,
        string sourcePath,
        string? parentName
    )
    {
        for (int i = 0; i < descriptor.Value.Count; i++)
        {
            var value = descriptor.Value[i];
            string path = $"{descriptor.Name}.{value.Name}";
            result.TryAdd(
                path,
                new SchemaEntry(
                    path,
                    value.Options?.Deprecated == true,
                    file.Name,
                    Line(lines, $"{sourcePath}.2.{i}")
                ) { CsharpName = (parentName is null ? descriptor.Name : parentName + ".Types." + descriptor.Name)
                    + "." + PascalCase(value.Name) }
            );
        }
    }
}
