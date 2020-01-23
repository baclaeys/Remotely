﻿using Remotely.Shared.Models;
using Remotely.ScreenCast.Core;
using Remotely.ScreenCast.Core.Services;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Remotely.Shared.Win32;
using System.Threading;
using Remotely.ScreenCast.Win.Services;
using Remotely.ScreenCast.Core.Interfaces;
using Remotely.ScreenCast.Win.Capture;
using Remotely.ScreenCast.Core.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Remotely.ScreenCast.Win
{
    public class Program
	{
        public static Conductor Conductor { get; private set; }
        public static CursorIconWatcher CursorIconWatcher { get; private set; }
        public static IServiceProvider Services => ServiceContainer.Instance;

        private static string CurrentDesktopName { get; set;
        }
        public static async void CursorIconWatcher_OnChange(object sender, CursorInfo cursor)
        {
            if (Conductor?.CasterSocket != null)
            {
                await Conductor.CasterSocket.SendCursorChange(cursor, Conductor.Viewers.Keys.ToList());
            }
        }

        public static void Main(string[] args)
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                BuildServices();

                CursorIconWatcher = Services.GetRequiredService<CursorIconWatcher>();

                Conductor = Services.GetRequiredService<Conductor>();
                Conductor.ProcessArgs(args);

                Conductor.Connect().ContinueWith(async (task) =>
                {
                    await Conductor.CasterSocket.SendDeviceInfo(Conductor.ServiceID, Environment.MachineName, Conductor.DeviceID);
                    if (Win32Interop.GetCurrentDesktop(out var currentDesktopName))
                    {
                        Logger.Write($"Setting initial desktop to {currentDesktopName}.");
                        if (Win32Interop.SwitchToInputDesktop())
                        {
                            CurrentDesktopName = currentDesktopName;
                        }
                        else
                        {
                            Logger.Write("Failed to set initial desktop.");
                        }
                    }
                    else
                    {
                        Logger.Write("Failed to get initial desktop name.");
                    }
                    await CheckForRelaunch();
                    Services.GetRequiredService<IdleTimer>().Start();
                    CursorIconWatcher.OnChange += CursorIconWatcher_OnChange;
                    Services.GetRequiredService<IClipboardService>().BeginWatching();
                });
               
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
                throw;
            }
        }

        private static void BuildServices()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging(builder =>
            {
                builder.AddConsole().AddEventLog();
            });

            serviceCollection.AddSingleton<CursorIconWatcher>();
            serviceCollection.AddSingleton<IScreenCaster, WinScreenCaster>();
            serviceCollection.AddSingleton<IKeyboardMouseInput, WinInput>();
            serviceCollection.AddSingleton<IClipboardService, WinClipboardService>();
            serviceCollection.AddSingleton<IAudioCapturer, WinAudioCapturer>();
            serviceCollection.AddSingleton<CasterSocket>();
            serviceCollection.AddSingleton<IdleTimer>();
            serviceCollection.AddSingleton<Conductor>();
            serviceCollection.AddTransient<ICapturer>(provider => {
                try
                {
                    return new DXCapture();
                }
                catch
                {
                    return new BitBltCapture();
                }
            });

            ServiceContainer.Instance = serviceCollection.BuildServiceProvider();
        }

        private static async Task CheckForRelaunch()
        {

            if (Conductor.ArgDict.ContainsKey("relaunch"))
            {
                Logger.Write($"Resuming after relaunch in desktop {CurrentDesktopName}.");
                var viewersString = Conductor.ArgDict["viewers"];
                var viewerIDs = viewersString.Split(",".ToCharArray());
                await Conductor.CasterSocket.NotifyViewersRelaunchedScreenCasterReady(viewerIDs);
            }
            else
            {
                await Conductor.CasterSocket.NotifyRequesterUnattendedReady(Conductor.RequesterID);
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Write((Exception)e.ExceptionObject);
        }

    }
}
