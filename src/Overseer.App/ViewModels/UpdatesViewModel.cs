using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overseer.Core;

namespace Overseer.App.ViewModels;

/// <summary>Settings → Updates: check GitHub for a newer release and, where possible, download and install it.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    readonly MainViewModel main;
    readonly bool loading;
    ReleaseInfo? release;
    CancellationTokenSource? cts;

    public UpdatesViewModel(MainViewModel main)
    {
        this.main = main;
        loading = true;
        CheckDaily = main.Config.CheckForUpdates;
        loading = false;
        UpdateLastChecked();
    }

    public string CurrentVersion => "Overseer " + Updater.CurrentText;
    public static bool CanInstall => Updater.CanInstallItself();
    /// <summary>How updates are applied on this copy, in plain words.</summary>
    public string HowText => CanInstall
        ? "Updates download from GitHub, are checked against their published SHA-256, and install over this copy. Overseer restarts by itself."
        : OperatingSystem.IsWindows()
            ? "This copy wasn't installed with the Overseer installer, so updates open the download page instead."
            : "On this system updates open the download page; download the file for your computer and replace this copy.";

    [ObservableProperty] bool checkDaily;
    [ObservableProperty] bool isChecking;
    [ObservableProperty] bool isDownloading;
    [ObservableProperty] double progress;
    [ObservableProperty] string status = "";
    [ObservableProperty] string lastChecked = "";
    [ObservableProperty] bool hasUpdate;
    [ObservableProperty] string newVersion = "";
    [ObservableProperty] string notes = "";
    [ObservableProperty] bool failed;

    public bool IsBusy => IsChecking || IsDownloading;
    public string InstallLabel => CanInstall ? "Download and install" : "Open download page";
    public bool HasNotes => Notes.Length > 0;

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    partial void OnCheckDailyChanged(bool value)
    {
        if (loading) return;
        main.Config.CheckForUpdates = value;
        main.Config.Save();
        if (value) _ = CheckIfDue();
    }

    void UpdateLastChecked() =>
        LastChecked = main.Config.LastUpdateCheck is { } t ? "Last checked " + Text.Ago(t.LocalDateTime) : "Not checked yet";

    /// <summary>Called at startup: checks quietly when the daily check is on and the last check was over a day ago.</summary>
    public async Task CheckIfDue()
    {
        if (!main.Config.CheckForUpdates) return;
        if (main.Config.LastUpdateCheck is { } t && DateTimeOffset.UtcNow - t < TimeSpan.FromHours(23)) return;
        await Check(quiet: true);
    }

    [RelayCommand]
    Task CheckNow() => Check(quiet: false);

    async Task Check(bool quiet)
    {
        if (IsBusy) return;
        IsChecking = true; Failed = false;
        Status = "Checking GitHub for a newer version…";
        try
        {
            using var http = Updater.CreateClient();
            var r = await Updater.Latest(http);
            main.Config.LastUpdateCheck = DateTimeOffset.UtcNow;
            main.Config.Save();
            UpdateLastChecked();
            if (r == null || !Updater.IsNewer(r))
            {
                release = null; HasUpdate = false; Notes = "";
                Status = "You're up to date.";
                if (!quiet) main.Toast("Overseer is up to date (" + Updater.CurrentText + ").", true);
                return;
            }
            release = r;
            NewVersion = Updater.Text(r.Version);
            Notes = Updater.PlainNotes(r.Notes);
            HasUpdate = true;
            Status = $"Version {NewVersion} is available. You have {Updater.CurrentText}.";
            main.Toast($"Overseer {NewVersion} is available.", true, () => main.GoUpdates(), "View");
        }
        catch (Exception ex)
        {
            Failed = true;
            Status = "Couldn't check for updates: " + Friendly(ex);
            if (!quiet) main.Toast(Status, false);
        }
        finally { IsChecking = false; }
    }

    [RelayCommand]
    async Task Install()
    {
        if (release == null || IsBusy) return;
        var installer = CanInstall ? Updater.Installer(release) : null;
        if (installer == null) { ProcessControl.Open(release.PageUrl); return; }

        if (!await main.ConfirmAsync($"Download Overseer {NewVersion} ({installer.Size / 1_048_576.0:0} MB) and install it? Overseer will close and reopen by itself.", "Download and install"))
            return;

        IsDownloading = true; Failed = false; Progress = 0;
        Status = "Downloading…";
        cts = new CancellationTokenSource();
        try
        {
            using var http = Updater.CreateClient();
            var path = await Updater.Download(http, release, installer, new Progress<double>(p => { Progress = p * 100; Status = $"Downloading… {p:P0}"; }), cts.Token);
            Status = "Checksum verified. Installing…";
            Process.Start(Updater.InstallerStart(path));
            main.QuitForUpdate();
        }
        catch (OperationCanceledException)
        {
            Status = "Download cancelled.";
        }
        catch (Exception ex)
        {
            Failed = true;
            Status = "Couldn't install the update: " + Friendly(ex);
            main.Toast(Status, false);
        }
        finally { IsDownloading = false; cts?.Dispose(); cts = null; }
    }

    [RelayCommand] void Cancel() => cts?.Cancel();
    [RelayCommand] void OpenReleases() => ProcessControl.Open(release?.PageUrl ?? Updater.ReleasesPage);

    static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } h when h.InnerException is System.Net.Sockets.SocketException => "no internet connection.",
        TaskCanceledException => "GitHub took too long to answer.",
        System.ComponentModel.Win32Exception => "the installer didn't start (it may have been blocked).",
        _ => ex.Message.TrimEnd('.') + ".",
    };
}
