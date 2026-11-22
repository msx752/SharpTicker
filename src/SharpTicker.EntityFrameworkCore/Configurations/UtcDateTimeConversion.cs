using System;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SharpTicker.EntityFrameworkCore.Configurations
{
    /// <summary>
    /// SharpTicker writes the times of the store's tables in UTC. Most providers read a DateTime column back as
    /// <see cref="DateTimeKind.Unspecified"/>, and an Unspecified time given back to the managers is read in the
    /// scheduler time zone. This converter writes the value as it is and reads it back as UTC: an Unspecified value keeps
    /// its ticks, a Local one is converted to the same instant in UTC. The column type
    /// stays the one of a DateTime, so no migration is needed.
    /// A property that already has a conversion (an application's own convention for DateTime, set in
    /// <c>ConfigureConventions</c>) keeps it, so its column type does not change either.
    /// </summary>
    internal static class UtcDateTimeConversion
    {
        internal static readonly ValueConverter<DateTime, DateTime> UtcOnRead = new(
            value => value,
            // A provider that returns Local (Npgsql with its legacy timestamp behaviour) gives the instant in local time.
            value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));

        internal static PropertyBuilder<DateTime> ReadAsUtc(this PropertyBuilder<DateTime> property)
            => HasOwnConversion(property) ? property : property.HasConversion(UtcOnRead);

        internal static PropertyBuilder<DateTime?> ReadAsUtc(this PropertyBuilder<DateTime?> property)
            => HasOwnConversion(property) ? property : property.HasConversion(UtcOnRead);

        private static bool HasOwnConversion(PropertyBuilder property)
            => property.Metadata.GetValueConverter() != null || property.Metadata.GetProviderClrType() != null;
    }
}
