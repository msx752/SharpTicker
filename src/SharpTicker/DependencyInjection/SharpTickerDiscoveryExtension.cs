using System.Reflection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.DependencyInjection;

public static class SharpTickerDiscoveryExtension
{
    private const string GeneratedClassSuffix = "SharpTickerInstanceFactoryExtensions";
    
    /// <summary>
    /// Loads the assemblies to initialize the source generated code.
    /// </summary>
    public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddSharpTickerDiscovery<TTimeTicker, TCronTicker>(
        this TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration, 
        Assembly[] assemblies)
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        var assembliesToLoad = assemblies ?? [];

        foreach (var assembly in assembliesToLoad)
        {
            if(!string.IsNullOrEmpty(assembly.FullName))
                Assembly.Load(assembly.FullName);
        }
        
        return tickerConfiguration;
    }
}