namespace Radar;

public sealed record SchemaChange(
    string Kind,
    string Path,
    string? FromType,
    string? ToType,
    string? FromLocation,
    string? ToLocation
);

public static class SchemaDiff
{
    public static IReadOnlyList<SchemaChange> Compare(SchemaModel from, SchemaModel to)
    {
        var changes = new List<SchemaChange>();
        foreach (var (path, oldField) in from.Fields)
        {
            if (!to.Fields.TryGetValue(path, out var newField))
            {
                changes.Add(
                    new SchemaChange(
                        "FieldRemoved",
                        path,
                        oldField.ProtoType,
                        null,
                        oldField.Location,
                        null
                    )
                );
                continue;
            }
            if (oldField.ProtoType != newField.ProtoType || oldField.Repeated != newField.Repeated)
                changes.Add(
                    new SchemaChange(
                        "FieldTypeChanged",
                        path,
                        TypeLabel(oldField),
                        TypeLabel(newField),
                        oldField.Location,
                        newField.Location
                    )
                );
            if (!oldField.Deprecated && newField.Deprecated)
                changes.Add(
                    new SchemaChange(
                        "FieldNewlyDeprecated",
                        path,
                        oldField.ProtoType,
                        newField.ProtoType,
                        oldField.Location,
                        newField.Location
                    )
                );
        }
        foreach (var (path, field) in to.Fields)
            if (!from.Fields.ContainsKey(path))
                changes.Add(
                    new SchemaChange(
                        "FieldAdded",
                        path,
                        null,
                        field.ProtoType,
                        null,
                        field.Location
                    )
                );

        CompareEntries(changes, from.Enums, to.Enums, "EnumValueRemoved", "EnumValueAdded");
        CompareEntries(
            changes,
            from.Services,
            to.Services,
            "ServiceMethodRemoved",
            "ServiceMethodAdded"
        );
        return changes
            .OrderBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string TypeLabel(SchemaField field) =>
        field.Repeated ? field.ProtoType + "[]" : field.ProtoType;

    private static void CompareEntries(
        List<SchemaChange> changes,
        IReadOnlyDictionary<string, SchemaEntry> from,
        IReadOnlyDictionary<string, SchemaEntry> to,
        string removedKind,
        string addedKind
    )
    {
        foreach (var (path, entry) in from)
            if (!to.ContainsKey(path))
                changes.Add(new SchemaChange(removedKind, path, null, null, entry.Location, null));
        foreach (var (path, entry) in to)
            if (!from.ContainsKey(path))
                changes.Add(new SchemaChange(addedKind, path, null, null, null, entry.Location));
    }
}
