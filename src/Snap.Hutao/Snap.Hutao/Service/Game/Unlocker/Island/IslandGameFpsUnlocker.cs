﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Core;
using Snap.Hutao.Win32.Foundation;
using Snap.Hutao.Win32.UI.WindowsAndMessaging;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Windows.Storage;
using static Snap.Hutao.Win32.ConstValues;
using static Snap.Hutao.Win32.Kernel32;
using static Snap.Hutao.Win32.Macros;
using static Snap.Hutao.Win32.User32;

namespace Snap.Hutao.Service.Game.Unlocker.Island;

internal sealed class IslandGameFpsUnlocker : GameFpsUnlocker
{
    private const string IslandEnvironmentName = "4F3E8543-40F7-4808-82DC-21E48A6037A7";

    private readonly string dataFolderIslandPath;

    public IslandGameFpsUnlocker(IServiceProvider serviceProvider, Process gameProcess, in UnlockOptions options, IProgress<GameFpsUnlockerContext> progress)
        : base(serviceProvider, gameProcess, options, progress)
    {
        RuntimeOptions runtimeOptions = serviceProvider.GetRequiredService<RuntimeOptions>();
        dataFolderIslandPath = Path.Combine(runtimeOptions.DataFolder, "Snap.Hutao.UnlockerIsland.dll");
    }

    protected override async ValueTask PostUnlockOverrideAsync(GameFpsUnlockerContext context, LaunchOptions launchOptions, ILogger logger, CancellationToken token = default(CancellationToken))
    {
        try
        {
            File.Copy(InstalledLocation.GetAbsolutePath("Snap.Hutao.UnlockerIsland.dll"), dataFolderIslandPath, true);
        }
        catch
        {
            context.Logger.LogError("Failed to copy island file.");
            return;
        }

        try
        {
            using (MemoryMappedFile file = MemoryMappedFile.CreateOrOpen(IslandEnvironmentName, 1024))
            {
                using (MemoryMappedViewAccessor accessor = file.CreateViewAccessor())
                {
                    nint handle = accessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
                    UpdateIslandEnvironment(handle, context, launchOptions);
                    InitializeIsland(context.GameProcess);

                    using (PeriodicTimer timer = new(context.Options.AdjustFpsDelay))
                    {
                        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        {
                            context.Logger.LogInformation("context.GameProcess.HasExited: {Value}", context.GameProcess.HasExited);
                            if (!context.GameProcess.HasExited && context.FpsAddress != 0U)
                            {
                                IslandEnvironmentView view = UpdateIslandEnvironment(handle, context, launchOptions);
                                context.Logger.LogDebug("Island Environment|{State}|{Error}", view.State, view.LastError);
                                context.Report();
                            }
                            else
                            {
                                context.IsUnlockerValid = false;
                                context.FpsAddress = 0;
                                context.Report();
                                return;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            context.Logger.LogInformation("Exit PostUnlockOverrideAsync");
        }
    }

    private unsafe void InitializeIsland(Process gameProcess)
    {
        HANDLE hModule = default;
        try
        {
            hModule = NativeLibrary.Load(dataFolderIslandPath);
            nint pIslandGetWindowHook = NativeLibrary.GetExport((nint)(hModule & ~0x3L), "IslandGetWindowHook");

            HOOKPROC hookProc = default;
            ((delegate* unmanaged[Stdcall]<HOOKPROC*, HRESULT>)pIslandGetWindowHook)(&hookProc);

            SpinWait.SpinUntil(() => gameProcess.MainWindowHandle is not 0);
            uint threadId = GetWindowThreadProcessId(gameProcess.MainWindowHandle, default);
            HHOOK hHook = SetWindowsHookExW(WINDOWS_HOOK_ID.WH_GETMESSAGE, hookProc, (HINSTANCE)hModule, threadId);
            if (hHook.Value is 0)
            {
                Marshal.ThrowExceptionForHR(HRESULT_FROM_WIN32(GetLastError()));
            }

            if (!PostThreadMessageW(threadId, WM_NULL, default, default))
            {
                Marshal.ThrowExceptionForHR(HRESULT_FROM_WIN32(GetLastError()));
            }
        }
        finally
        {
            NativeLibrary.Free(hModule);
        }
    }

    private unsafe IslandEnvironmentView UpdateIslandEnvironment(nint handle, GameFpsUnlockerContext context, LaunchOptions launchOptions)
    {
        IslandEnvironment* pIslandEnvironment = (IslandEnvironment*)handle;
        pIslandEnvironment->Address = context.FpsAddress;
        pIslandEnvironment->Value = launchOptions.TargetFps;

        return *(IslandEnvironmentView*)pIslandEnvironment;
    }
}