using System;
using System.Threading.Tasks;
using OpenFreqClient.Models;

namespace OpenFreqClient.Services;

public interface IConfigurationService : IDisposable
{
    Task<AppConfiguration> LoadConfigurationAsync();
    Task SaveConfigurationAsync(AppConfiguration config);
}
