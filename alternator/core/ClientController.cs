﻿namespace guildwars2.tools.alternator;

public class ClientController
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly FileInfo loginFile;
    private readonly FileInfo gfxSettingsFile;
    private readonly LaunchType launchType;
    private readonly SemaphoreSlim loginSemaphore;
    private readonly DirectoryInfo applicationFolder;
    private readonly Settings settings;

    public event EventHandler<GenericEventArgs<bool>>? AfterLaunchAccount;

    public ClientController(DirectoryInfo applicationFolder, Settings settings, FileInfo loginFile,
        FileInfo gfxSettingsFile, LaunchType launchType)
    {
        this.applicationFolder = applicationFolder;
        this.settings = settings;
        this.loginFile = loginFile;
        this.gfxSettingsFile = gfxSettingsFile;
        this.launchType = launchType;
        loginSemaphore = new SemaphoreSlim(0, 1);
    }

    public async Task LaunchMultiple(List<Account> accounts, int maxInstances, CancellationToken launchCancelled)
    {
        if (!accounts.Any())
        {
            Logger.Debug("No accounts to run.");
            return;
        }

        try
        {
            Logger.Debug("Max GW2 Instances={0}", maxInstances);
            var exeSemaphore = new SemaphoreSlim(0, maxInstances);
            var launchCount = new Counter();
            var tasks = accounts.Select(account => Task.Run(async () =>
                {
                    var launcher = new Launcher(account, launchType, applicationFolder, settings, launchCancelled);
                    var success = await launcher.Launch(loginFile, gfxSettingsFile, loginSemaphore, exeSemaphore, 3, launchCount);
                    AfterLaunchAccount?.Invoke(account, new GenericEventArgs<bool>(success));
                    LogManager.Flush();
                }, launchCancelled))
                .ToList();
            Logger.Debug("{0} threads primed.", tasks.Count);
            // Allow all the tasks to start and block.
            await Task.Delay(200, launchCancelled);
            if (launchCancelled.IsCancellationRequested) return;

            // Release the hounds
            exeSemaphore.Release(maxInstances);
            loginSemaphore.Release(1);

            await Task.WhenAll(tasks.ToArray());
            Logger.Info("All launch tasks finished.");
        }
        finally
        {
            await Restore();
            Logger.Info("GW2 account files restored.");
        }
    }

    private async Task Restore()
    {
        Logger.Info("{0} login semaphore={1}", nameof(Restore), loginSemaphore.CurrentCount);
        await loginSemaphore.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                SafeRestoreBackup(loginFile);
                SafeRestoreBackup(gfxSettingsFile);
            });
        }
        finally
        {
            loginSemaphore.Release();
        }
    }

    private void SafeRestoreBackup(FileInfo file)
    {
        var backup = new FileInfo($"{file.FullName}.bak");
        if (!backup.Exists) return;
        try
        {
            file.Delete(); // Symbolic links need to be specifically delete
            backup.MoveTo(file.FullName);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Could not restore {file} from backup!");
        }
    }
}