using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.EntityFrameworkCore.Configurations
{
    public class CronTickerOccurrenceConfigurations<TCronTicker> : IEntityTypeConfiguration<CronTickerOccurrenceEntity<TCronTicker>>
        where TCronTicker : CronTickerEntity
    {
        private readonly string _schema;

        public CronTickerOccurrenceConfigurations(string schema = Constants.DefaultSchema)
            => _schema = schema;
        
        public void Configure(EntityTypeBuilder<CronTickerOccurrenceEntity<TCronTicker>> builder)
        {
            builder.HasKey("Id");
            
            builder.Property(e => e.Id)
                .ValueGeneratedNever();
            
            builder.Property(x => x.LockHolder)
                .IsRequired(false);

            builder.Property(x => x.ExecutionTime).ReadAsUtc();
            builder.Property(x => x.LockedAt).ReadAsUtc();
            builder.Property(x => x.ExecutedAt).ReadAsUtc();
            builder.Property(x => x.CreatedAt).ReadAsUtc();
            builder.Property(x => x.UpdatedAt).ReadAsUtc();
            
            builder.HasIndex("CronTickerId")
                .HasDatabaseName("IX_CronTickerOccurrence_CronTickerId");

            builder.HasIndex("ExecutionTime")
                .HasDatabaseName("IX_CronTickerOccurrence_ExecutionTime");

            builder.HasIndex("Status", "ExecutionTime")
                .HasDatabaseName("IX_CronTickerOccurrence_Status_ExecutionTime");

            builder.HasOne(x => x.CronTicker)
                .WithMany()
                .HasForeignKey(x => x.CronTickerId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex("CronTickerId", "ExecutionTime")
                .IsUnique()
                .HasDatabaseName("UQ_CronTickerId_ExecutionTime");

            builder.ToTable("CronTickerOccurrences", _schema);
        }
    }
}