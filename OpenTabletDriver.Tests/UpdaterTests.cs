using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Castle.Core.Internal;
using Moq;
using Moq.Protected;
using OpenTabletDriver.Desktop.Updater;
using Xunit;

namespace OpenTabletDriver.Tests
{
    using MockUpdater = Updater<UpdateInfo>;

    public class UpdaterTests
    {
        // TODO: Reintroduce updater test removed in c0626309 to platform test project

        // The default update version of the mock updater
        private static readonly Version MockUpdateVersion = new(1, 0);

        public static TheoryData<Version?, bool> UpdaterBase_ProperlyChecks_Version_Async_Data => new()
        {
            // Outdated
            { new Version("0.1.0.0"), true },
            // Same version
            { MockUpdateVersion, false },
            // From the future
            { new Version("99.0.0.0"), false },
        };

        [Theory]
        [MemberData(nameof(UpdaterBase_ProperlyChecks_Version_Async_Data))]
        public Task Updater_ProperlyChecks_Version_Async(Version version, bool expectedUpdateStatus)
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                updaterEnv.Version = version;
                var mockUpdater = CreateMockUpdater(updaterEnv).Object;

                var hasUpdate = await mockUpdater.CheckForUpdates();

                Assert.Equal(expectedUpdateStatus, hasUpdate);
            });
        }

        [Theory]
        [MemberData(nameof(UpdaterBase_ProperlyChecks_Version_Async_Data))]
        public Task Updater_PreventsUpdate_WhenAlreadyUpToDate_Async(Version version, bool expectedUpdateStatus)
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                updaterEnv.Version = version;
                var mockUpdater = CreateMockUpdater(updaterEnv);
                var hasInstalledUpdate = false;

                // Track calls to Updater.PostInstall
                mockUpdater.Protected()
                    .Setup("PostInstall")
                    .Callback(() => hasInstalledUpdate = true);

                var mockUpdaterObject = mockUpdater.Object;

                await mockUpdaterObject.InstallUpdate();

                Assert.Equal(expectedUpdateStatus, hasInstalledUpdate);
            });
        }

        [Fact]
        public Task Updater_AllowsReupdate_WhenInstallFailed_Async()
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                var mockUpdater = CreateMockUpdater(updaterEnv);
                mockUpdater.Protected()
                    .Setup("PostInstall")
                    .Callback(() => throw new InvalidOperationException());
                var mockUpdaterObject = mockUpdater.Object;
                var beforeUpdate = await mockUpdaterObject.CheckForUpdates();

                await Assert.ThrowsAsync<InvalidOperationException>(mockUpdaterObject.InstallUpdate);
                var afterUpdate = await mockUpdaterObject.CheckForUpdates();

                Assert.True(beforeUpdate, "Updater.HasUpdate has returned false before update was installed.");
                Assert.True(afterUpdate, "Updater.HasUpdate has returned false after unsuccessful update process.");
            });
        }

        [Fact]
        public Task Updater_HasUpdateReturnsFalse_During_UpdateInstall_Async()
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                var mockUpdaterObject = CreateMockUpdater(updaterEnv).Object;
                var beforeUpdate = await mockUpdaterObject.CheckForUpdates();

                var updateTask = mockUpdaterObject.InstallUpdate();
                var duringUpdate = await mockUpdaterObject.CheckForUpdates();

                await updateTask;
                Assert.True(beforeUpdate, "Updater.HasUpdate has returned false before update was installed.");
                Assert.False(duringUpdate, "Updater.HasUpdate has returned true during update process.");
            });
        }

        [Fact]
        public Task Updater_HasUpdateReturnsFalse_After_UpdateInstall_Async()
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                var mockUpdaterObject = CreateMockUpdater(updaterEnv).Object;
                var beforeUpdate = await mockUpdaterObject.CheckForUpdates();

                await mockUpdaterObject.InstallUpdate();
                var afterUpdate = await mockUpdaterObject.CheckForUpdates();

                Assert.True(beforeUpdate, "Updater.HasUpdate has returned false before update was installed.");
                Assert.False(afterUpdate, "Updater.HasUpdate has returned true after update is installed.");
            });
        }

        [Fact]
        public Task Updater_Prevents_ConcurrentAndConsecutive_Updates_Async()
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                var mockUpdater = CreateMockUpdater(updaterEnv);
                var callCount = 0;

                // Track call count of Updater.Install
                mockUpdater.Protected()
                    .Setup("PostInstall")
                    .Callback(() => Interlocked.Increment(ref callCount));

                var mockUpdaterObject = mockUpdater.Object;
                var parallelTasks = new[]
                {
                    Task.Run(mockUpdaterObject.InstallUpdate),
                    Task.Run(mockUpdaterObject.InstallUpdate),
                    Task.Run(mockUpdaterObject.InstallUpdate),
                    Task.Run(mockUpdaterObject.InstallUpdate)
                };

                await Task.WhenAll(parallelTasks);

                Assert.Equal(1, callCount);
            });
        }

        [Fact]
        public Task Updater_ProperlyBackups_BinAndAppDataDirectory_Async()
        {
            return MockEnvironmentAsync(async updaterEnv =>
            {
                var mockUpdater = CreateMockUpdater(updaterEnv).Object;
                var wpfFile = Encoding.UTF8.GetBytes("OpenTabletDriver.UX.Wpf");
                var daemonFile = Encoding.UTF8.GetBytes("OpenTabletDriver.Daemon");
                var settingsFile = Encoding.UTF8.GetBytes("settings.json");
                var pluginFile = Encoding.UTF8.GetBytes("Plugin.dll");

                var fakeBinaryFiles = new Dictionary<string, byte[]>
                {
                    ["OpenTabletDriver.UX.Wpf"] = wpfFile,
                    ["OpenTabletDriver.Daemon"] = daemonFile
                };
                var fakeAppDataFiles = new Dictionary<string, byte[]>
                {
                    ["settings.json"] = settingsFile,
                    ["Plugins/SomePlugin/Plugin.dll"] = pluginFile
                };
                await SetupFakeBinaryFilesAsync(updaterEnv, fakeBinaryFiles, fakeAppDataFiles);

                await mockUpdater.InstallUpdate();

                await VerifyFakeBinaryFilesAsync(updaterEnv, mockUpdater, fakeBinaryFiles, fakeAppDataFiles);
            });
        }

        private static void InitializeDirectory(string directory)
        {
            CleanDirectory(directory);
            Directory.CreateDirectory(directory);
        }

        private static void CleanDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }

        private static async Task MockEnvironmentAsync(Func<UpdaterEnvironment, Task> asyncAction)
        {
            var mockUpdaterEnv = new UpdaterEnvironment
            {
                Version = new Version("0.1.0.0"),
                BinaryDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName()),
                AppDataDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName())
            };

            mockUpdaterEnv.RollBackDir = Path.Join(mockUpdaterEnv.AppDataDir, "Temp");

            InitializeDirectory(mockUpdaterEnv.BinaryDir);
            InitializeDirectory(mockUpdaterEnv.AppDataDir);
            InitializeDirectory(mockUpdaterEnv.RollBackDir);

            try
            {
                await asyncAction(mockUpdaterEnv);
            }
            finally
            {
                CleanDirectory(mockUpdaterEnv.BinaryDir);
                CleanDirectory(mockUpdaterEnv.AppDataDir);
                CleanDirectory(mockUpdaterEnv.RollBackDir);
            }
        }

        private static Mock<MockUpdater> CreateMockUpdater(UpdaterEnvironment updaterEnv, Version? mockVersion = null)
        {
            var mockUpdater = new Mock<MockUpdater>(updaterEnv.Version, updaterEnv.BinaryDir, updaterEnv.AppDataDir, updaterEnv.RollBackDir) { CallBase = true };
            mockUpdater.Protected()
                .Setup<Task<UpdateInfo>>("GetUpdate")
                .Returns(Task.FromResult(new UpdateInfo(mockVersion ?? MockUpdateVersion)));

            return mockUpdater;
        }

        private static async Task SetupFakeBinaryFilesAsync(UpdaterEnvironment updaterEnv,
            Dictionary<string, byte[]>? fakeBinaryFiles = null,
            Dictionary<string, byte[]>? fakeAppDataFiles = null)
        {
            await SetupFakeFilesAsync(updaterEnv.BinaryDir, fakeBinaryFiles);
            await SetupFakeFilesAsync(updaterEnv.AppDataDir, fakeAppDataFiles);
        }

        private static async Task SetupFakeFilesAsync(string? rootDir, Dictionary<string, byte[]>? fakeFiles)
        {
            if (rootDir == null || fakeFiles == null)
                return;

            foreach (var kv in fakeFiles)
            {
                var splits = kv.Key.Split('/');
                var directory = Path.Join(splits[..^1]);
                var file = splits[^1];

                var targetDirectory = Path.Join(rootDir, directory);

                if (!directory.IsNullOrEmpty() && !Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                await File.WriteAllBytesAsync(Path.Join(targetDirectory, file), kv.Value);
            }
        }

        private static async Task VerifyFakeBinaryFilesAsync<TUpdateInfo>(
            UpdaterEnvironment updaterEnv,
            Updater<TUpdateInfo> updater,
            Dictionary<string, byte[]>? fakeBinaryFiles = null,
            Dictionary<string, byte[]>? fakeAppDataFiles = null
        ) where TUpdateInfo : UpdateInfo
        {
            var rollbackDir = updater.VersionedRollbackDirectory!;
            await VerifyFakeFilesAsync(updaterEnv.BinaryDir, Path.Join(rollbackDir, "bin"), fakeBinaryFiles);
            await VerifyFakeFilesAsync(updaterEnv.AppDataDir, Path.Join(rollbackDir, "appdata"), fakeAppDataFiles);
        }

        private static async Task VerifyFakeFilesAsync(string? rootDir, string? rollBackDir, Dictionary<string, byte[]>? fakeFiles)
        {
            if (rootDir == null || rollBackDir == null || fakeFiles == null)
                return;

            foreach (var kv in fakeFiles)
            {
                var splits = kv.Key.Split('/');
                var directory = Path.Join(splits[..^1]);
                var file = splits[^1];

                var targetFile = Path.Join(rollBackDir, directory, file);
                Assert.True(File.Exists(targetFile), $"{kv.Key} does not exist in rollback store");

                var fileContent = await File.ReadAllBytesAsync(targetFile);
                Assert.Equal(kv.Value, fileContent);
            }
        }

        private struct UpdaterEnvironment
        {
            public Version? Version;
            public string? BinaryDir;
            public string? AppDataDir;
            public string? RollBackDir;
        }
    }
}
