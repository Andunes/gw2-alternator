﻿namespace guildwars2.tools.alternator;

public class ClientController
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly LaunchType launchType;
    private readonly SemaphoreSlim loginSemaphore;
    private readonly DirectoryInfo applicationFolder;
    private readonly ISettingsController settingsController;
    private readonly AuthenticationThrottle authenticationThrottle;
    private readonly IVpnCollection vpnCollection;

    public event EventHandler? MetricsUpdated;


    public ClientController(DirectoryInfo applicationFolder,
        ISettingsController settingsController,
        AuthenticationThrottle authenticationThrottle,
        IVpnCollection vpnCollection,
        LaunchType launchType)
    {
        this.applicationFolder = applicationFolder;
        this.settingsController = settingsController;
        this.launchType = launchType;
        this.authenticationThrottle = authenticationThrottle;
        this.vpnCollection = vpnCollection;

        readyClients = new List<Client>();
        loginSemaphore = new SemaphoreSlim(0, 1);
    }

    private record VpnAccounts(VpnDetails Vpn, List<IAccount> Accounts);

    public async Task LaunchMultiple(
        List<IAccount> selectedAccounts,
        IAccountCollection accountCollection,
        bool all,
        bool shareArchive,
        bool ignoreVpn,
        int maxInstances,
        int vpnAccountSize,
        CancellationTokenSource cancellationTokenSource
        )
    {
        readyClients.Clear();
        vpnCollection.ResetConnections();

        var accounts = selectedAccounts.Any() ? selectedAccounts : accountCollection.AccountsToRun(launchType, all);

        if (accounts == null || !accounts.Any())
        {
            Logger.Debug("No accounts to run.");
            return;
        }

        foreach (var account in accounts)
        {
            account.Done = false;
        }

        var vpnsUsed = new List<VpnDetails>();
        var clients = new List<Client>();
        var first = true;
        var start = DateTime.UtcNow;
        try
        {
            var exeSemaphore = new SemaphoreSlim(0, maxInstances);
            Logger.Debug("Max GW2 Instances={0}", maxInstances);

            var accountsByVpn = AccountCollection.AccountsByVpn(accounts, ignoreVpn);
            while (accounts.Any(a => !a.Done))
            {
                var now = DateTime.UtcNow;

                var accountsByVpnDetails = accountsByVpn
                    .Select(kv => new VpnAccounts(vpnCollection.GetVpn(kv.Key), kv.Value.Where(a => !a.Done).ToList()))
                    .Where(d => d.Accounts.Any())
                    .ToList();

                var vpnSets = accountsByVpnDetails
                    .OrderBy(s => s.Vpn.Available(now.Subtract(new TimeSpan(1,0,0)), launchType==LaunchType.Update))
                    .ThenBy(s => s.Vpn.RecentFailures)
                    .ThenBy(s => s.Vpn.GetPriority(s.Accounts.Count, vpnAccountSize))
                    .ToList();

                Logger.Debug("{0} launch sets found", vpnSets.Count);

                var (vpn, vpnAccounts) = vpnSets.First();

                var accountsAvailableToLaunch = vpnAccounts
                    .OrderBy(a => a.Available(now)).ToList();

                var accountsToLaunch = accountsAvailableToLaunch
                    .OrderBy(a => a.VpnPriority)
                    .Take(vpnAccountSize)
                    .ToList();

                Logger.Debug("{0} VPN Chosen with {1} accounts", vpn.DisplayId, accountsToLaunch.Count);

                if (!accountsToLaunch.Any()) continue;

                var clientsToLaunch = new List<Client>();
                foreach (var account in accountsToLaunch)
                {
                    Logger.Debug("Launching client for Account {0}", account.Name);
                    clientsToLaunch.Add(await account.NewClient(applicationFolder.FullName));
                }
                clients.AddRange(clientsToLaunch);

                var waitUntil = vpn.Available(now, launchType == LaunchType.Update).Subtract(now);
                if (waitUntil.TotalSeconds > 0)
                {
                    Logger.Debug("VPN {0} on login cooldown {1}", vpn.Id, waitUntil);
                    await Task.Delay(waitUntil, cancellationTokenSource.Token);
                }

                try
                {
                    vpn.Cancellation = new CancellationTokenSource();
                    var vpnToken = vpn.Cancellation.Token;
                    vpnToken.ThrowIfCancellationRequested();
                    var doubleTrouble = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, vpnToken);
                    if (!vpnsUsed.Contains(vpn)) vpnsUsed.Add(vpn);
                    var status = await vpn.Connect(cancellationTokenSource.Token);
                    if (status != null)
                    {
                        Logger.Error("VPN {0} Connection {1} : {2}", vpn.Id, vpn.ConnectionName, status);
                        continue;
                    }

                    Logger.Debug("Launching {0} clients", clientsToLaunch.Count);
                    var tasks = PrimeLaunchTasks(vpn, clientsToLaunch, shareArchive, exeSemaphore, doubleTrouble.Token);
                    if (cancellationTokenSource.IsCancellationRequested) return;

                    if (first)
                    {
                        await Task.Delay(200, cancellationTokenSource.Token);
                        if (cancellationTokenSource.IsCancellationRequested) return;
                        // Release the hounds
                        exeSemaphore.Release(maxInstances);
                        loginSemaphore.Release(1);
                        first = false;
                    }

                    await Task.WhenAll(tasks.ToArray());
                }
                catch (OperationCanceledException)
                {
                    authenticationThrottle.Reset();
                    if (cancellationTokenSource.IsCancellationRequested) return;
                    Logger.Debug("VPN {0} failure detected, skipping", vpn.Id);
                }
                finally
                {
                    var status = await vpn.Disconnect(cancellationTokenSource.Token);
                    if (status != null)
                    {
                        Logger.Error("VPN {0} Disconnection failed : {1}", vpn, status);
                    }
                    else
                    {
                        authenticationThrottle.CurrentVpn = null;
                    }
                }
            }

            Logger.Info("All launch tasks finished.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "LaunchMultiple: unexpected error");
        }
        finally
        {
            cancellationTokenSource.Cancel(true);
            await Restore(first);
            Logger.Info("GW2 account files restored.");
            await SaveMetrics(start, clients, vpnsUsed);
        }
    }

    private async Task SaveMetrics(DateTime startOfRun, List<Client> clients, List<VpnDetails> vpnDetailsList)
    {
        Logger.Debug("Metrics being saved");

        (string, DateTime) AddOffset(DateTime reference, DateTime time, string line)
        {
            if (time < reference) return (line + "\t", reference);
            line += $"\t{time.Subtract(reference).TotalSeconds.ToString(CultureInfo.InvariantCulture)}";
            return (line, time);
        }

        var lines = new List<string>
        {
            $"Started\t{startOfRun:d}\t{startOfRun:T}", 
            $"Total Time\t{DateTime.UtcNow.Subtract(startOfRun).TotalSeconds}\ts",
            "Account\tStart\tAuthenticate\tLogin\tEnter\tExit",
        };

        foreach (var client in clients.Where(c => c.Account.Name != null && c.StartAt > DateTime.MinValue).OrderBy(c => c.StartAt))
        {
            Logger.Debug("Client {0} {1} {2}", client.Account.Name, client.AccountIndex, client.StartAt);
            var line = client.Account.Name;
            var reference = startOfRun;
            (line, reference) = AddOffset(reference, client.StartAt, line!);
            (line, reference) = AddOffset(reference, client.AuthenticationAt, line);
            (line, reference) = AddOffset(reference, client.LoginAt, line);
            (line, reference) = AddOffset(reference, client.EnterAt, line);
            (line, reference) = AddOffset(reference, client.ExitAt, line);
            lines.Add(line);
        }

        foreach (var connection in vpnDetailsList
                     .Where(v => !string.IsNullOrEmpty(v.Id))
                     .SelectMany(v => v.Connections)
                     .Where(c => c.ConnectMetrics != null)
                     .OrderBy(c => c.ConnectMetrics!.StartAt))
        {
            var line = $"VPN-{connection.Id}";
            var reference = startOfRun;
            (line, reference) = AddOffset(reference, connection.ConnectMetrics!.StartAt, line);
            line += "\t";
            (line, reference) = AddOffset(reference, connection.ConnectMetrics!.FinishAt, line);
            if (connection.DisconnectMetrics != null)
            {
                (line, reference) = AddOffset(reference, connection.DisconnectMetrics!.StartAt, line);
                (line, reference) = AddOffset(reference, connection.DisconnectMetrics!.FinishAt, line);
            }
            lines.Add(line);
        }

        var metricsFile = settingsController.MetricsFile;
        await File.WriteAllLinesAsync(metricsFile, lines);
        var metricsFileUniquePath = settingsController.UniqueMetricsFile;
        File.Copy(metricsFile, metricsFileUniquePath);
        Logger.Info("Metrics saved to {0}", metricsFileUniquePath);
        MetricsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private List<Task> PrimeLaunchTasks(
        VpnDetails vpnDetails, 
        IEnumerable<Client> clients,
        bool shareArchive,
        SemaphoreSlim exeSemaphore,
        CancellationToken cancellationToken)
    {
        var tasks = clients.Select(client => Task.Run(async () =>
            {
                var launcher = new Launcher(client, launchType, applicationFolder, settingsController.Settings!, vpnDetails, cancellationToken);
                launcher.ClientReady += LauncherClientReady;
                launcher.ClientClosed += LauncherClientClosed;
                _ = await launcher.LaunchAsync(
                    settingsController.DatFile!, 
                    applicationFolder, 
                    settingsController.GfxSettingsFile!, 
                    shareArchive, 
                    authenticationThrottle, 
                    loginSemaphore, 
                    exeSemaphore
                    );
                LogManager.Flush();
            }, cancellationToken))
            .ToList();
        Logger.Debug("{0} launch tasks primed.", tasks.Count);
        return tasks;
    }

    private List<Client> readyClients { get; }
    private Client? activeClient;
    private void LauncherClientReady(object? sender, EventArgs e)
    {
        if (sender is not Client client) return;
        if (!readyClients.Contains(client)) readyClients.Add(client);
        if (activeClient != null) return;
        activeClient = client;
        activeClient.RestoreWindow();
    }

    private void LauncherClientClosed(object? sender, EventArgs e)
    {
        if (sender is not Client client) return;
        if (readyClients.Contains(client)) readyClients.Remove(client);
        if (activeClient != client) return;
        var next = readyClients.FirstOrDefault();
        if (next == null)
        {
            activeClient = null;
            return;
        }
        activeClient = next;
        activeClient.RestoreWindow();
    }

    private async Task Restore(bool first)
    {
        var obtainedLoginLock = false;
        if (!first)
        {
            Logger.Debug("{0} login semaphore={1}", nameof(Restore), loginSemaphore.CurrentCount);
            obtainedLoginLock = await loginSemaphore.WaitAsync(new TimeSpan(0, 2, 0));
            if (!obtainedLoginLock) Logger.Error("{0} login semaphore wait timed-out", nameof(Restore));
        }

        try
        {
            await Task.Run(() =>
            {
                SafeRestoreBackup(settingsController.DatFile!);
                SafeRestoreBackup(settingsController.GfxSettingsFile!);
            });
        }
        finally
        {
            if (obtainedLoginLock) loginSemaphore.Release();
        }
    }

    private void SafeRestoreBackup(FileInfo file)
    {
        var backup = new FileInfo($"{file.FullName}.bak");
        if (!backup.Exists) return;
        try
        {
            file.Delete(); // Symbolic links need to be specifically deleted
            backup.MoveTo(file.FullName);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Could not restore {file} from backup!");
        }
    }
}