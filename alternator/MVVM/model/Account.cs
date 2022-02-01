﻿using System.Text.Json.Serialization;

namespace guildwars2.tools.alternator.MVVM.model;

public interface IAccount
{
    string Name { get; }
    string? DisplayName { get; set; }
    string? Character { get; }
    string? LoginFilePath { get; set; }
    string? ApiKey { get; }
    ObservableCollectionEx<Currency>? Counts { get; set; }
    ObservableCollectionEx<string>? VPN { get; }
    bool HasVPN { get; }
    DateTime LastLogin { get; set; }
    DateTime LastCollection { get; set; }
    DateTime CreatedAt { get; }
    bool LoginRequired { get; }
    bool CollectionRequired { get; }
    bool UpdateRequired { get; }
    Task SwapFilesAsync(FileInfo gw2LocalDat, FileInfo gw2GfxSettings, FileInfo referenceGfxSettingsFile);
    int? GetCurrency(string currencyName);
    void SetCount(string countName, int value);
    event PropertyChangedEventHandler? PropertyChanged;
    // Client
    Client NewClient();
    Client? CurrentClient { get; }
    int Attempt { get; }
    RunState RunStatus { get; }
    string? StatusMessage { get; }
    bool Done { get; set; }
    void UpdateVpn(VpnDetails vpn, bool isChecked);
}

[Serializable]
[DebuggerDisplay("{" + nameof(DebugDisplay) + ",nq}")]
public class Account : ObservableObject, IAccount
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private string name;
    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    private string? displayName;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName
    {
        get => displayName;
        set => SetProperty(ref displayName, value);
    }

    private string? character;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Character
    {
        get => character;
        set => SetProperty(ref character, value);
    }

    private string? loginFilePath;
    public string? LoginFilePath
    {
        get => loginFilePath;
        set => SetProperty(ref loginFilePath, value);
    }

    private string? apiKey;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey
    {
        get => apiKey;
        set => SetProperty(ref apiKey, value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollectionEx<Currency>? Counts { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservableCollectionEx<string>? VPN { get; set; }


    public void UpdateVpn(VpnDetails vpn, bool isChecked)
    {
        if (VPN == null)
        {
            if (!isChecked) return;
            VPN = new ObservableCollectionEx<string>();
        }

        if (isChecked)
        {
            if (!VPN.Contains(vpn.Id)) VPN.Add(vpn.Id);
        }
        else
        {
            if (VPN.Contains(vpn.Id)) VPN.Remove(vpn.Id);
        }


    }

    [JsonIgnore]
    public bool HasVPN => VPN?.Any() ?? false;

    private DateTime lastLogin;

    public DateTime LastLogin
    {
        get => lastLogin;
        set
        {
            if (SetProperty(ref lastLogin, value))
            {
                OnPropertyChanged(nameof(LoginRequired));
            }
        }
    }

    private DateTime lastCollection;
    public DateTime LastCollection
    {
        get => lastCollection;
        set
        {
            if (SetProperty(ref lastCollection, value))
            {
                OnPropertyChanged(nameof(CollectionRequired));
            }
        }
    }

    private DateTime createdAt;
    public DateTime CreatedAt
    {
        get => createdAt;
        set
        {
            if (SetProperty(ref createdAt, value))
            {
                OnPropertyChanged(nameof(CollectionRequired));
            }
        }
    }

    private Counter attempt;
    [JsonIgnore]
    public int Attempt => attempt.Count;

    public Account()
    {
        attempt = new Counter();
        LastLogin = DateTime.MinValue;
        LastCollection = DateTime.MinValue;
        CreatedAt = DateTime.Now;
    }

    public Account(string? name) : this()
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public Account(string? name, string? character, string? loginFilePath) : this(name)
    {
        Character = character;
        LoginFilePath = loginFilePath;
    }

    private string DebugDisplay => $"{Name} ({Character}) {LastLogin} {LastCollection}";

    private Client? currentClient;
    [JsonIgnore]
    public Client? CurrentClient
    {
        get => currentClient!;
        private set
        {
            if (SetProperty(ref currentClient, value))
            {
                OnPropertyChanged(nameof(Attempt));
                OnPropertyChanged(nameof(RunStatus));
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
    }

    [JsonIgnore]
    public RunState RunStatus => CurrentClient?.RunStatus ?? RunState.Unset;
    [JsonIgnore]
    public string? StatusMessage => CurrentClient?.StatusMessage;

    private bool done;
    [JsonIgnore]
    public bool Done
    {
        get => done;
        set => SetProperty(ref done, value);
    }

    public Client NewClient()
    {
        attempt.Increment();
        CurrentClient = new Client(this);
        CurrentClient.PropertyChanged += (_, args) => OnPropertyChanged(args.PropertyName);
        OnPropertyChanged(nameof(Attempt));
        return CurrentClient;
    }

    [JsonIgnore]
    public bool LoginRequired => LastLogin < DateTime.UtcNow.Date;

    [JsonIgnore]
    public bool CollectionRequired => LastCollection < DateTime.UtcNow.Date && DateTime.UtcNow.Date.Subtract(LastCollection).TotalDays > 30;

    [JsonIgnore]
    public bool UpdateRequired => true;


    public const int MysticCoinId = 19976;
    public const int LaurelId = 3;

    public async Task FetchAccountDetails(CancellationToken cancellationToken)
    {
        Logger.Debug("{0} Fetching details from GW2 API", Name);

        var apiConnection = new Gw2Sharp.Connection(ApiKey!);
        using var apiClient = new Gw2Sharp.Gw2Client(apiConnection);
        var webApiClient = apiClient.WebApi.V2;

        var accountReturnTask = webApiClient.Account.GetAsync(cancellationToken);
        var charactersReturnTask = webApiClient.Characters.AllAsync(cancellationToken);
        var walletReturnTask = webApiClient.Account.Wallet.GetAsync(cancellationToken);
        var bankReturnTask = webApiClient.Account.Bank.GetAsync(cancellationToken);
        var materialsReturnTask = webApiClient.Account.Materials.GetAsync(cancellationToken);

        var accountReturn = await accountReturnTask;
        CreatedAt = accountReturn.Created.UtcDateTime;
        DisplayName = accountReturn.Name;

        var wallet = await walletReturnTask;

        int laurelCount = wallet.FirstOrDefault(c => c is {Id: LaurelId})?.Value ?? 0;

        var characters = await charactersReturnTask;
        var prime = characters.FirstOrDefault();
        int mysticCoinCount = 0;
        if (prime != null)
        {
            Character = prime.Name;

            var allSlots = prime.Bags?
                .SelectMany(bag => bag?.Inventory ?? Array.Empty<Gw2Sharp.WebApi.V2.Models.AccountItem>())
                .Where(i => i != null).ToList();

            mysticCoinCount += (allSlots?.Where(i => i is {Id: MysticCoinId}).Sum(i => i!.Count)).GetValueOrDefault(0);
            // Bags of Mystic Coins
            mysticCoinCount += (allSlots?.Where(i => i is {Id: 68332}).Sum(i => i!.Count)).GetValueOrDefault(0) * 2;
            mysticCoinCount += (allSlots?.Where(i => i is {Id: 68318}).Sum(i => i!.Count)).GetValueOrDefault(0) * 4;
            mysticCoinCount += (allSlots?.Where(i => i is {Id: 68330}).Sum(i => i!.Count)).GetValueOrDefault(0) * 6;
            mysticCoinCount += (allSlots?.Where(i => i is {Id: 68333}).Sum(i => i!.Count)).GetValueOrDefault(0) * 8;

            // Bags of Laurels
            laurelCount += (allSlots?.Where(i => i is {Id: 68314}).Sum(i => i!.Count)).GetValueOrDefault(0);
            laurelCount += (allSlots?.Where(i => i is {Id: 68339}).Sum(i => i!.Count)).GetValueOrDefault(0) * 2;
            laurelCount += (allSlots?.Where(i => i is {Id: 68327}).Sum(i => i!.Count)).GetValueOrDefault(0) * 3;
            laurelCount += (allSlots?.Where(i => i is {Id: 68336}).Sum(i => i!.Count)).GetValueOrDefault(0) * 4;
            laurelCount += (allSlots?.Where(i => i is {Id: 68328}).Sum(i => i!.Count)).GetValueOrDefault(0) * 10;
            laurelCount += (allSlots?.Where(i => i is {Id: 68334}).Sum(i => i!.Count)).GetValueOrDefault(0) * 15;
            laurelCount += (allSlots?.Where(i => i is {Id: 68351}).Sum(i => i!.Count)).GetValueOrDefault(0) * 20;
            // Chest of Loyalty
            laurelCount += (allSlots?.Where(i => i is {Id: 68326}).Sum(i => i!.Count)).GetValueOrDefault(0) * 20;
        }

        var bank = await bankReturnTask;
        var mysticCoinInBank = bank.Where(i => i is {Id: MysticCoinId}).Sum(i => i.Count);
        mysticCoinCount += mysticCoinInBank;

        var materials = await materialsReturnTask;
        mysticCoinCount += (materials.FirstOrDefault(m => m is {Id: MysticCoinId})?.Count).GetValueOrDefault(0);

        SetCount("MysticCoin", mysticCoinCount);
        SetCount("Laurel", laurelCount);

        Logger.Debug("{0} {1} has {2} Laurels and {3} Mystic Coins", Name, Character, laurelCount, mysticCoinCount);

    }

    private void LinkFiles(FileSystemInfo from, FileSystemInfo to)
    {
        if (from.Exists)
        {
            if (from.LinkTarget != null)
            {
                from.Delete();
            }
            else
            {
                File.Move(from.FullName, $"{from.FullName}.bak", true);
            }
        }

        // Symbolic link creation requires process to be Admin
        from.CreateAsSymbolicLink(to.FullName);
        Logger.Debug("{0} {1} file linked to: {2}", Name, to.Extension, to.FullName);
    }

    public async Task SwapFilesAsync(FileInfo gw2LocalDat, FileInfo gw2GfxSettings, FileInfo referenceGfxSettingsFile)
    {
        await Task.Run(() =>
        {
            if (loginFilePath != null) LinkFiles(gw2LocalDat, new FileInfo(loginFilePath));
        });
        await Task.Run(() =>
        {
            LinkFiles(gw2GfxSettings, referenceGfxSettingsFile);
        });
        await Task.Delay(200);
    }

    public int? GetCurrency(string currencyName)
    {
        return Counts?.FirstOrDefault(c => c.Name.Equals(currencyName, StringComparison.InvariantCultureIgnoreCase))?.Count;
    }

    public void SetCount(string countName, int value)
    {
        Counts ??= new ObservableCollectionEx<Currency>();

        var count = Counts.FirstOrDefault(c => string.Equals(c.Name, countName, StringComparison.OrdinalIgnoreCase));
        if (count != null)
        {
            if (count.Count != value)
            {
                count.Count = value;
                OnPropertyChanged($"{count.Name}Count");
            }
        }
        else
        {
            Counts.Add(new Currency(countName, value));
            OnPropertyChanged($"{countName}Count");
        }
    }

}