using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.EntityFrameworkCore.Configurations
{
    public class CronTickerConfigurations<TCronTicker> : IEntityTypeConfiguration<TCronTicker>
        where TCronTicker : CronTickerEntity, new()
    {
        private readonly string _schema;

        public CronTickerConfigurations(string schema = Constants.DefaultSchema)
            => _schema = schema;

        public void Configure(EntityTypeBuilder<TCronTicker> builder)
        {
            builder.HasKey("Id");
                
            builder.Property(e => e.Id)
                .ValueGeneratedNever();

            builder.HasIndex("Expression")
                .HasDatabaseName("IX_CronTickers_Expression");

            // Index for common lookups by function + expression
            builder.HasIndex("Function", "Expression")
                .HasDatabaseName("IX_Function_Expression");

            builder.Property(e => e.IsEnabled)
                .IsRequired();

            // Deprecated column, no longer used by the scheduler. Still mapped
            // so existing databases need no migration.
#pragma warning disable CS0618
            builder.Property(e => e.IsSystemPaused)
                .IsRequired()
                .HasDefaultValue(false);
#pragma warning restore CS0618

            builder.ToTable("CronTickers", _schema);
        }
    }
}
