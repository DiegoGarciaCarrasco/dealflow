using DealFlow.Core;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.FeatureManagement;

namespace DealFlow.Worker;

/// <summary>Adapts Microsoft.FeatureManagement to the Core IFeatureFlags interface and refreshes App Configuration.</summary>
public sealed class FeatureManagerFlags(IVariantFeatureManager features, IConfigurationRefresherProvider? refreshers = null) : IFeatureFlags
{
    public async Task<bool> IsEnabledAsync(string flag)
    {
        // Functions have no HTTP middleware to trigger refresh, so refresh (cheaply, respecting the interval) on read.
        foreach (var refresher in refreshers?.Refreshers ?? [])
            await refresher.TryRefreshAsync();

        return await features.IsEnabledAsync(flag);
    }
}
