using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Services;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqClient.Services.Audio;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using Serilog;

namespace OpenFreqClient.Services;

/// <summary>
/// Extension methods for configuring dependency injection
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenFreqServices(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IRtcClientFactory, OpenFreqRtcClientFactory>();
        services.AddSingleton<IPlaybackServiceFactory, RadioPlaybackServiceFactory>();
        services.AddSingleton<ISignalCalculatorFactory, TerrainSignalCalculatorFactory>();
        services.AddSingleton<IOpenFreqService, OpenFreqService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IAcmiClientService, AcmiClientService>();
        services.AddSingleton<IFalconSharedMemoryService, FalconSharedMemoryService>();
        services.AddSingleton<IFalconRadioSharedMemoryService, FalconRadioSharedMemoryService>();
        services.AddSingleton<IIvcMonitorService, IvcMonitorService>();

        // Register ViewModels
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ChannelCardListViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services;
    }
}
