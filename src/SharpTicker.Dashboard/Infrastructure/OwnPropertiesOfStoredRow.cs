using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpTicker.Utilities.Entities.BaseEntity;

namespace SharpTicker.Dashboard.Infrastructure;

/// <summary>
/// The dashboard's edit dialog sends the properties of the library's entity types only, and an update writes every
/// property of the instance it is given (EF Core writes every column). A property that an application's own ticker type
/// adds would be emptied by an edit that does not send it. Before an update, each public settable property declared on
/// the application's type, not on the library's entity types, that the request body does not name keeps its stored value.
/// A body that names it still sets it. A property marked <see cref="JsonIgnoreAttribute"/> is never read from a body, so it
/// always keeps its stored value (#95).
/// </summary>
internal static class OwnPropertiesOfStoredRow<T> where T : class
{
    /// <summary>The application's own properties of <typeparamref name="T"/>; empty for the library's entity types.</summary>
    internal static readonly PropertyInfo[] Properties = Find();

    internal static void KeepTheOnesTheBodyDoesNotSend(T fromBody, T stored, string json, JsonSerializerOptions options)
    {
        if (Properties.Length == 0 || fromBody == null || stored == null)
            return;

        var sent = NamesInBody(json);
        foreach (var property in Properties)
        {
            if (!IsIgnored(property) && sent.Contains(JsonNameOf(property, options)))
                continue;

            property.SetValue(fromBody, property.GetValue(stored));
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2090",
        Justification = "A property the trimmer removed from the application's type is read and written by nothing, so there is nothing to keep.")]
    private static PropertyInfo[] Find()
    {
        var library = typeof(BaseTickerEntity).Assembly;
        return typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                        && p.GetMethod is { IsPublic: true }
                        && p.SetMethod is { IsPublic: true }
                        && p.GetMethod.GetBaseDefinition().DeclaringType?.Assembly != library)
            .ToArray();
    }

    private static bool IsIgnored(PropertyInfo property)
        => property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };

    private static string JsonNameOf(PropertyInfo property, JsonSerializerOptions options)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
           ?? options.PropertyNamingPolicy?.ConvertName(property.Name)
           ?? property.Name;

    // The dashboard's options read names case-insensitively, so the body names a property in any casing.
    private static HashSet<string> NamesInBody(string json)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in document.RootElement.EnumerateObject())
                names.Add(member.Name);
        }

        return names;
    }
}
