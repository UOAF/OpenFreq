using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.DependencyInjection;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Services.Acmi;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views;

namespace OpenFreqClient;

public partial class App : Application
{
    private List<ILifecycleService>? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {

            // Get services from DI
            var serviceProvider = Program.ServiceProvider;
            if (serviceProvider is null)
            {
                throw new InvalidOperationException("Service provider not initialized");
            }

#if WINDOWS
        _services =
        [
            serviceProvider.GetRequiredService<IFalconRadioSharedMemoryService>(),
            serviceProvider.GetRequiredService<IFalconSharedMemoryService>(),
            serviceProvider.GetRequiredService<IAcmiClientService>(),
            serviceProvider.GetRequiredService<IHotkeyService>()
        ];
#else
            _services = new List<ILifecycleService>
            {
                serviceProvider.GetRequiredService<IAcmiClientService>(),
                serviceProvider.GetRequiredService<IHotkeyService>(),
            };
#endif

            // Start services
            foreach (var service in _services)
            {
                service.Start();
            }

            var mainViewModel = Program.ServiceProvider?.GetService<MainWindowViewModel>()
                                ?? throw new InvalidOperationException("Service provider not initialized");
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };

            desktop.ShutdownRequested += async (s, e) =>
            {
                // Defer shutdown until we're done cleaning up
                e.Cancel = true;

                foreach (var service in _services)
                {
                    service.Stop();
                }

                await mainViewModel.DisposeAsync();

                // Now actually shutdown
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
