﻿using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Linq;

namespace RomM.Games
{
    internal class RomMInstallController : InstallController
    {
        protected readonly IRomM _romM;
        protected CancellationTokenSource _watcherToken;
        public ILogger Logger => LogManager.GetLogger();

        internal RomMInstallController(Game game, IRomM romM) : base(game)
        {
            Name = "Download";
            _romM = romM;
        }

        public override void Dispose()
        {
            _watcherToken?.Cancel();
            base.Dispose();
        }

        public override void Install(InstallActionArgs args)
        {
            var info = Game.GetRomMGameInfo();

            var dstPath = info.Mapping?.DestinationPathResolved ??
                throw new Exception("Mapped emulator data cannot be found, try removing and re-adding.");

            _watcherToken = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    // Fetch file from content endpoint
                    HttpResponseMessage response = await RomM.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    List<GameRom> roms = new List<GameRom>();
                    string installDir = Path.Combine(dstPath, Path.GetFileNameWithoutExtension(info.FileName));
                    string gamePath = Path.Combine(installDir, info.FileName);
                    if (info.IsMulti)
                    {
                        // File name for multi-file archives is the folder name, so we append .zip
                        gamePath = Path.Combine(installDir, info.FileName + ".zip");
                    }

                    if (_romM.Playnite.ApplicationInfo.IsPortable)
                    {
                        installDir = installDir.Replace(_romM.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                        gamePath = gamePath.Replace(_romM.Playnite.Paths.ApplicationPath, ExpandableVariables.PlayniteDirectory);
                    }

                    Logger.Debug($"Downloading {Game.Name} to {gamePath}.");
                    Directory.CreateDirectory(installDir);

                    // Stream the file directly to disk
                    using (var fileStream = new FileStream(gamePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var httpStream = await response.Content.ReadAsStreamAsync())
                    {
                        await httpStream.CopyToAsync(fileStream);
                    }

                    Logger.Debug($"Download of {Game.Name} complete.");

                    // ALways extract top-level file of multi-file archives
                    if (info.IsMulti || (info.Mapping.AutoExtract && IsFileCompressed(gamePath)))
                    {
                        // Extract the archive to the install directory
                        ExtractArchive(gamePath, installDir);

                        // Delete the compressed file
                        File.Delete(gamePath);

                        // Extract nested archives if auto-extract is enabled
                        if (info.IsMulti && info.Mapping.AutoExtract)
                        {
                            ExtractNestedArchives(installDir);
                        }

                        // Add just the ROM files to the list
                        string[] actualRomFiles = Directory.GetFiles(installDir, "*", SearchOption.AllDirectories)
                            .Where(file => !file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                            .ToArray();

                        foreach (var romFile in actualRomFiles) {
                            roms.Add(new GameRom(Game.Name, romFile));
                        }
                    } else {
                        // Add the single ROM file to the list
                        roms.Add(new GameRom(Game.Name, gamePath));
                    }

                    // Update the game's installation status
                    var game = _romM.Playnite.Database.Games[Game.Id];
                    game.IsInstalled = true;
                    _romM.Playnite.Database.Games.Update(game);

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData()
                    {
                        InstallDirectory = installDir,
                        Roms = roms,
                    }));
                }
                catch (Exception ex)
                {
                    _romM.Playnite.Notifications.Add(Game.GameId, $"Failed to download {Game.Name}.{Environment.NewLine}{Environment.NewLine}{ex}", NotificationType.Error);
                    Game.IsInstalling = false;
                    throw;
                }
            });
        }

        private static bool IsFileCompressed(string filePath)
        {
            try
            {
                // Try to open the file as an archive
                using (var archive = ArchiveFactory.Open(filePath))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void ExtractArchive(string gamePath, string installDir)
        {
            using (var archive = ArchiveFactory.Open(gamePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory)
                    {
                        entry.WriteToDirectory(installDir, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }
            }
        }

        void ExtractNestedArchives(string directoryPath)
        {
            foreach (var file in Directory.GetFiles(directoryPath))
            {   
                if (IsFileCompressed(file))
                {
                    ExtractArchive(file, directoryPath);
                    File.Delete(file);
                }
            }
        }
    }
}
