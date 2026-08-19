using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Flow.Launcher.Plugin;
using GrepFlow.Presentation;

namespace GrepFlow.Search;

public sealed class RipgrepInstaller
{
    private readonly IPublicAPI _api;
    private readonly ITextProvider _texts;
    private readonly RipgrepExecutable _executable;
    private readonly RipgrepLocator _locator;
    private readonly PluginLog _log;
    private int _installing;

    public RipgrepInstaller(
        IPublicAPI api,
        ITextProvider texts,
        RipgrepExecutable executable,
        RipgrepLocator locator,
        PluginLog log)
    {
        _api = api;
        _texts = texts;
        _executable = executable;
        _locator = locator;
        _log = log;
    }

    public async Task<bool> PromptAndInstallAsync()
    {
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            _log.Info(nameof(RipgrepInstaller), "install already in progress; ignoring concurrent request");
            return false;
        }

        try
        {
            if (!ConfirmInstall())
                return false;

            await InstallAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            _log.Info(nameof(RipgrepInstaller), "install canceled");
            return false;
        }
        catch (Exception exception)
        {
            _log.Error(nameof(RipgrepInstaller), "install failed", exception);
            _api.ShowMsgError(
                _texts.Get(TextKeys.PluginGrepflowPluginName),
                _texts.Get(TextKeys.PluginGrepflowRipgrepInstallFailed, exception.Message));
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    private bool ConfirmInstall()
    {
        var title = _texts.Get(TextKeys.PluginGrepflowRipgrepInstallConfirmTitle);
        var body = _texts.Get(TextKeys.PluginGrepflowRipgrepInstallConfirmBody);

        var result = _api.ShowMsgBox(
            body,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private async Task InstallAsync()
    {
        if (!RipgrepRelease.TryResolveAsset(out var triple, out var zipUrl, out var expectedSha256))
        {
            var arch = RuntimeInformation.OSArchitecture.ToString();
            var message = _texts.Get(TextKeys.PluginGrepflowRipgrepUnsupportedArch, arch);
            _log.Warn(nameof(RipgrepInstaller), message);
            _api.ShowMsgError(_texts.Get(TextKeys.PluginGrepflowPluginName), message);
            return;
        }

        _log.Info(nameof(RipgrepInstaller), $"downloading ripgrep {RipgrepRelease.Version} ({triple})");

        var tempRoot = Path.Combine(Path.GetTempPath(), "GrepFlow", "rg-download", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "ripgrep.zip");
        using var cts = new CancellationTokenSource();

        try
        {
            Directory.CreateDirectory(tempRoot);

            var installed = false;
            await _api.ShowProgressBoxAsync(
                _texts.Get(TextKeys.PluginGrepflowRipgrepInstalling),
                async reportProgress =>
                {
                    await _api.HttpDownloadAsync(zipUrl, zipPath, reportProgress, cts.Token)
                        .ConfigureAwait(false);

                    cts.Token.ThrowIfCancellationRequested();
                    await VerifySha256Async(zipPath, expectedSha256).ConfigureAwait(false);

                    cts.Token.ThrowIfCancellationRequested();
                    ExtractToInstallDir(zipPath);
                    installed = true;

                    reportProgress?.Invoke(100);
                },
                () => cts.Cancel()).ConfigureAwait(false);

            if (!installed)
            {
                if (cts.IsCancellationRequested)
                    _log.Info(nameof(RipgrepInstaller), "install canceled");
                return;
            }

            var targetPath = _locator.InstalledExecutablePath;
            _executable.Set(targetPath);
            _log.Info(nameof(RipgrepInstaller), $"ripgrep installed to {targetPath}");
            _api.ShowMsg(
                _texts.Get(TextKeys.PluginGrepflowPluginName),
                _texts.Get(TextKeys.PluginGrepflowRipgrepInstalled));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private void ExtractToInstallDir(string zipPath)
    {
        var installDir = _locator.InstallDirectory;
        Directory.CreateDirectory(installDir);

        var targetPath = _locator.InstalledExecutablePath;
        var stagingPath = targetPath + ".new";

        try
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
            ExtractRgExe(zipPath, stagingPath);

            // overwrite: true keeps the old target if Move fails (unlike delete-then-move).
            File.Move(stagingPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task VerifySha256Async(string zipPath, string expectedHex)
    {
        await using var stream = File.OpenRead(zipPath);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(hash);

        if (!actualHex.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw new InvalidOperationException(
                $"SHA256 mismatch for downloaded zip (expected {expectedHex}, got {actualHex}).");
        }
    }

    private static void ExtractRgExe(string zipPath, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(static e =>
            e.Name.Equals("rg.exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException("Downloaded zip does not contain rg.exe.");

        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
