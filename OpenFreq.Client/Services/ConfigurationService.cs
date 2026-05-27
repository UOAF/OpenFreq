using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreqClient.Models;

namespace OpenFreqClient.Services;

public class ConfigurationService(ILogger<ConfigurationService> logger) : IConfigurationService
{
    private static readonly string ConfigFilePath =
        Path.Combine(AppContext.BaseDirectory, "OpenFreq.Client.json");

    public async Task<AppConfiguration> LoadConfigurationAsync()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new AppConfiguration();
            }

            var json = await File.ReadAllTextAsync(ConfigFilePath);
            return Json.Json.Instance.Deserialize<AppConfiguration>(json)
                   ?? new AppConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to load configuration: {ExMessage}", ex.Message);
            return new AppConfiguration();
        }
    }

    public async Task SaveConfigurationAsync(AppConfiguration config)
    {
        try
        {
            var json = Json.Json.Instance.Serialize(config);
            await File.WriteAllTextAsync(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to save configuration: {ExMessage}", ex.Message);
        }
    }

    public void Dispose()
    {
        // nothing to do yet
    }
}
