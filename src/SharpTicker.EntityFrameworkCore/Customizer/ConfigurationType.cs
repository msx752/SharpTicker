namespace SharpTicker.EntityFrameworkCore.Customizer;

/// <summary>How the store's tables are added to an application's own context (<c>UseApplicationDbContext</c>).</summary>
public enum ConfigurationType
{
    /// <summary>SharpTicker adds the tables to the context's model.</summary>
    UseModelCustomizer,
    /// <summary>
    /// The context's <c>OnModelCreating</c> applies the configurations from <c>SharpTicker.EntityFrameworkCore.Configurations</c> itself.
    /// </summary>
    IgnoreModelCustomizer,
}