using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Radar;

// One public type of Microsoft.BingAds.dll. Kind: enum | class | struct | interface.
// Members: enum values, or public properties/fields/methods declared on the type itself.
public sealed record MsType(
    string Name,
    string Namespace,
    string Kind,
    string? BaseType,
    List<string> Members
)
{
    public string FullName => Namespace + "." + Name;
}

// The public surface of one Microsoft.BingAds.SDK version, read from the NuGet package itself
// (what our code compiles against), plus the Bulk CSV headers from the SDK source at that tag.
public sealed class MicrosoftSchema
{
    public required string Version { get; init; }
    public required List<MsType> Types { get; init; }
    public List<string> BulkHeaders { get; init; } = [];
    public bool BulkHeadersKnown { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public Dictionary<string, string> Dependencies { get; init; } = new();

    private Dictionary<string, List<MsType>>? byName;
    private Dictionary<string, MsType>? byFullName;

    public IReadOnlyList<MsType> Named(string name)
    {
        byName ??= Types
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        return byName.GetValueOrDefault(name) ?? [];
    }

    public MsType? Find(string fullName)
    {
        byFullName ??= Types
            .GroupBy(type => type.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return byFullName.GetValueOrDefault(fullName);
    }

    // Members declared on the type or any base type inside the SDK.
    public bool HasMember(MsType type, string member)
    {
        for (MsType? current = type; current is not null; current = Base(current))
            if (current.Members.Contains(member, StringComparer.Ordinal))
                return true;
        return false;
    }

    public MsType? Base(MsType type) =>
        type.BaseType is null
            ? null
            : Find(type.BaseType)
                ?? Named(type.BaseType.Split('.').Last()).FirstOrDefault(t => t.Kind != "enum");

    public bool DerivesFrom(MsType type, string baseName)
    {
        for (MsType? current = Base(type); current is not null; current = Base(current))
            if (current.Name == baseName)
                return true;
        return false;
    }
}

public static class MicrosoftSnapshot
{
    public const string PackageId = "Microsoft.BingAds.SDK";
    private const string RawSource =
        "https://raw.githubusercontent.com/BingAds/BingAds-dotNet-SDK/v{0}/BingAdsApiSDK/V13/Internal/Bulk/{1}";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // Cached at data/snapshots/microsoft/<version>.json; built from the .nupkg on first use.
    public static MicrosoftSchema Load(string root, string version, HttpClient? http = null)
    {
        string path = Path.Combine(root, "data", "snapshots", "microsoft", version + ".json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<MicrosoftSchema>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException($"Empty Microsoft snapshot: {path}");
        using var client = http is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : null;
        var web = http ?? client!;
        string package = Package(root, version, web);
        var schema = Build(package, version, web);
        AtomicFile.WriteProduced(
            path,
            () => JsonSerializer.SerializeToUtf8Bytes(schema, JsonOptions)
        );
        return schema;
    }

    public static List<string> StableVersions(HttpClient http)
    {
        using var document = JsonDocument.Parse(
            http.GetStringAsync(
                    "https://api.nuget.org/v3-flatcontainer/microsoft.bingads.sdk/index.json"
                )
                .GetAwaiter()
                .GetResult()
        );
        return document
            .RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .Where(item => !item.Contains('-') && System.Version.TryParse(item, out _))
            .OrderBy(item => System.Version.Parse(item))
            .ToList();
    }

    public static int Compare(string left, string right) =>
        System.Version.Parse(left).CompareTo(System.Version.Parse(right));

    private static string Package(string root, string version, HttpClient http)
    {
        string lower = version.ToLowerInvariant();
        string nugetCache = Path.Combine(
            Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget",
                    "packages"
                ),
            "microsoft.bingads.sdk",
            lower,
            $"microsoft.bingads.sdk.{lower}.nupkg"
        );
        if (File.Exists(nugetCache))
            return nugetCache;
        string cached = Path.Combine(
            root,
            "data",
            "cache",
            "nuget",
            $"microsoft.bingads.sdk.{lower}.nupkg"
        );
        if (!File.Exists(cached))
        {
            byte[] bytes = http.GetByteArrayAsync(
                    $"https://api.nuget.org/v3-flatcontainer/microsoft.bingads.sdk/{lower}/microsoft.bingads.sdk.{lower}.nupkg"
                )
                .GetAwaiter()
                .GetResult();
            AtomicFile.WriteProduced(cached, () => bytes);
        }
        return cached;
    }

    internal static MicrosoftSchema Build(string package, string version, HttpClient? http)
    {
        using var zip = ZipFile.OpenRead(package);
        var dll =
            zip.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith(
                    "/Microsoft.BingAds.dll",
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? throw new InvalidDataException($"No Microsoft.BingAds.dll in {package}");
        var memory = new MemoryStream();
        using (var stream = dll.Open())
            stream.CopyTo(memory);
        memory.Position = 0;
        var types = ReadTypes(memory);

        var nuspec = zip.Entries.First(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
        );
        XDocument manifest;
        using (var stream = nuspec.Open())
            manifest = XDocument.Load(stream);
        string notes =
            manifest
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "releaseNotes")
                ?.Value.Trim()
            ?? "";
        var dependencies = manifest
            .Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .GroupBy(e => (string?)e.Attribute("id") ?? "")
            .ToDictionary(
                g => g.Key,
                g => (string?)g.First().Attribute("version") ?? "",
                StringComparer.OrdinalIgnoreCase
            );

        List<string> headers = [];
        bool known = false;
        if (http is not null)
        {
            try
            {
                string table = http.GetStringAsync(
                        string.Format(RawSource, version, "StringTable.cs")
                    )
                    .GetAwaiter()
                    .GetResult();
                string csv = http.GetStringAsync(string.Format(RawSource, version, "CsvHeaders.cs"))
                    .GetAwaiter()
                    .GetResult();
                headers = BulkHeaders(table, csv);
                known = headers.Count > 0;
            }
            catch (HttpRequestException)
            {
                // Tag or file not published: headers stay unknown and the report says so.
            }
        }
        return new MicrosoftSchema
        {
            Version = version,
            Types = types,
            BulkHeaders = headers,
            BulkHeadersKnown = known,
            ReleaseNotes = notes,
            Dependencies = dependencies,
        };
    }

    // CsvHeaders.Headers lists StringTable.X constants; StringTable holds `public const string X = "Header";`.
    internal static List<string> BulkHeaders(string stringTable, string csvHeaders)
    {
        var constants = Regex
            .Matches(stringTable, @"public\s+const\s+string\s+(\w+)\s*=\s*""([^""]*)""")
            .GroupBy(match => match.Groups[1].Value)
            .ToDictionary(
                group => group.Key,
                group => group.First().Groups[2].Value,
                StringComparer.Ordinal
            );
        var block = Regex.Match(
            csvHeaders,
            @"public\s+static\s+readonly\s+string\[\]\s+Headers\s*=\s*\{(?<body>[\s\S]*?)\};"
        );
        if (!block.Success)
            return [];
        return Regex
            .Matches(block.Groups["body"].Value, @"StringTable\.(\w+)")
            .Select(match => constants.GetValueOrDefault(match.Groups[1].Value))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    internal static List<MsType> ReadTypes(Stream dll)
    {
        using var pe = new PEReader(dll);
        var reader = pe.GetMetadataReader();
        var types = new List<MsType>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            // ponytail: nested types skipped; the SDK's public API surface has none worth tracking.
            if (
                type.IsNested
                || (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public
            )
                continue;
            string ns = reader.GetString(type.Namespace);
            if (!ns.StartsWith("Microsoft.BingAds", StringComparison.Ordinal))
                continue;
            string name = reader.GetString(type.Name);
            int tick = name.IndexOf('`');
            if (tick > 0)
                name = name[..tick];
            string? baseType = TypeName(reader, type.BaseType);
            string kind =
                (type.Attributes & TypeAttributes.Interface) != 0 ? "interface"
                : baseType == "System.Enum" ? "enum"
                : baseType == "System.ValueType" ? "struct"
                : "class";
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
                    continue;
                if (kind == "enum" && (field.Attributes & FieldAttributes.Literal) == 0)
                    continue;
                members.Add(reader.GetString(field.Name));
            }
            if (kind != "enum")
            {
                foreach (var propertyHandle in type.GetProperties())
                {
                    var property = reader.GetPropertyDefinition(propertyHandle);
                    var accessors = property.GetAccessors();
                    var accessor = !accessors.Getter.IsNil ? accessors.Getter : accessors.Setter;
                    if (
                        !accessor.IsNil
                            && (
                                reader.GetMethodDefinition(accessor).Attributes
                                & MethodAttributes.MemberAccessMask
                            ) == MethodAttributes.Public
                        || kind == "interface"
                    )
                        members.Add(reader.GetString(property.Name));
                }
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (
                        (method.Attributes & MethodAttributes.MemberAccessMask)
                            != MethodAttributes.Public
                        || (method.Attributes & MethodAttributes.SpecialName) != 0
                    )
                        continue;
                    members.Add(reader.GetString(method.Name));
                }
            }
            types.Add(new MsType(name, ns, kind, baseType, members.ToList()));
        }
        return types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    private static string? TypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil)
            return null;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                return Join(
                    reader.GetString(definition.Namespace),
                    reader.GetString(definition.Name)
                );
            }
            case HandleKind.TypeReference:
            {
                var reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                return Join(
                    reader.GetString(reference.Namespace),
                    reader.GetString(reference.Name)
                );
            }
            case HandleKind.TypeSpecification:
                return null; // generic base (e.g. List<T>); not an SDK hierarchy we track
            default:
                return null;
        }

        static string Join(string ns, string name)
        {
            int tick = name.IndexOf('`');
            if (tick > 0)
                name = name[..tick];
            return ns.Length == 0 ? name : ns + "." + name;
        }
    }
}
