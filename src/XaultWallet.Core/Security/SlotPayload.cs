using System.Text.Json;
using System.Text.Json.Serialization;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>
/// The JSON sealed inside a vault slot.
///
/// Version 3 (current, 0.5) holds a whole <see cref="WalletProfile"/>: every wallet the password
/// opens, its address book, and which wallet was open last:
/// <code>{"v":3,"wipeOther":false,"active":"…","wallets":[{"id":…,"name":…,"type":0,"network":…,"mnemonic":…,…}],"contacts":[…]}</code>
/// Like version 2 it has no "real"/"decoy" marker: every slot has this same shape.
///
/// Version 2 (0.2–0.3) held one wallet:
/// <code>{"v":2,"network":..,"mnemonic":..,"seedOffset":..,"restoreHeight":..,"daemonAddress":..,"ephemeralWalletPassword":..,"wipeOther":false}</code>
///
/// Version 1 (0.1, no "v" field) also carried <c>kind</c> (0 real / 1 duress), <c>label</c>
/// ("Main" vs "Wallet") and <c>duressWipeReal</c>. Anyone holding the vault file plus the duress
/// password — exactly the coercion scenario the feature exists for — could decrypt the decoy and
/// read <c>"kind":1</c>, proving a hidden wallet.
///
/// v1 and v2 slots still read (as a one-wallet profile). A v1 slot is re-sealed as v3 the first
/// time its password opens it (to remove its marker); a v2 slot the first time 0.5 saves it, which
/// 0.3 then refuses as "created by a newer version".
/// </summary>
internal static class SlotPayload
{
    public const int CurrentVersion = 3;

    /// <summary>The 0.1 format: re-sealing it also removes its real/decoy marker.</summary>
    public const int Version01 = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize to the v3 schema. The caller owns (and must zero) the returned bytes.</summary>
    public static byte[] Serialize(WalletProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.SerializeToUtf8Bytes(new ProfileDto
        {
            Version = CurrentVersion,
            WipeOther = profile.WipeOtherSlotOnUnlock,
            Active = profile.ActiveWalletId,
            Wallets = profile.Wallets.Select(w => new WalletDto
            {
                Id = w.Id,
                Name = w.Name,
                Kind = w.Kind,
                Network = w.Network,
                Mnemonic = w.Mnemonic,
                SeedOffset = w.SeedOffset,
                Address = w.Address,
                ViewKey = w.ViewKey,
                SpendKey = w.SpendKey,
                RestoreHeight = w.RestoreHeight,
                DaemonAddress = w.DaemonAddress,
                EphemeralWalletPassword = w.EphemeralWalletPassword,
                SubaddressCounts = w.SubaddressCounts,
                Labels = w.Labels,
                AccountLabels = w.AccountLabels,
                TxNotes = w.TxNotes,
                Frozen = w.FrozenKeyImages,
            }).ToList(),
            Contacts = profile.Contacts.Select(c => new ContactDto { Id = c.Id, Name = c.Name, Address = c.Address, Note = c.Note }).ToList(),
        }, Json);
    }

    /// <summary>
    /// Parse a slot payload of any supported version. <paramref name="version"/> is the version it
    /// was written in (1, 2 or 3); anything below <see cref="CurrentVersion"/> should be re-sealed.
    /// </summary>
    /// <exception cref="InvalidDataException">Unparseable, empty, or written by a newer app version.</exception>
    public static WalletProfile Deserialize(ReadOnlySpan<byte> data, out int version)
    {
        ReadDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ReadDto>(data, Json);
        }
        catch (JsonException ex)
        {
            // The password was correct (GCM tag verified) but the payload didn't parse.
            throw new InvalidDataException("This vault was created by a different version of XaultWallet.", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException("Corrupt secrets payload.");
        }

        if (dto.Version is > CurrentVersion)
        {
            throw new InvalidDataException("This vault was created by a newer version of XaultWallet. Update the app to open it.");
        }

        version = dto.Version ?? Version01;
        return version >= 3 ? FromV3(dto) : FromSingleWallet(dto, version);
    }

    private static WalletProfile FromV3(ReadDto dto)
    {
        var profile = new WalletProfile
        {
            ActiveWalletId = dto.Active ?? string.Empty,
            WipeOtherSlotOnUnlock = dto.WipeOther,
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (WalletDto w in dto.Wallets ?? [])
        {
            string id = string.IsNullOrEmpty(w.Id) || !seen.Add(w.Id) ? WalletSecrets.NewId() : w.Id;
            seen.Add(id);
            profile.Wallets.Add(new WalletSecrets
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(w.Name) ? WalletSecrets.DefaultName : w.Name,
                Kind = w.Kind,
                Network = w.Network,
                Mnemonic = w.Mnemonic ?? string.Empty,
                SeedOffset = w.SeedOffset ?? string.Empty,
                Address = w.Address ?? string.Empty,
                ViewKey = w.ViewKey ?? string.Empty,
                SpendKey = w.SpendKey ?? string.Empty,
                RestoreHeight = w.RestoreHeight,
                DaemonAddress = w.DaemonAddress ?? string.Empty,
                EphemeralWalletPassword = w.EphemeralWalletPassword ?? string.Empty,
                SubaddressCounts = w.SubaddressCounts ?? new(),
                Labels = w.Labels ?? new(),
                AccountLabels = w.AccountLabels ?? new(),
                TxNotes = w.TxNotes ?? new(),
                FrozenKeyImages = (w.Frozen ?? []).Where(IsKeyImage).Distinct(StringComparer.Ordinal).ToList(),
            });
        }

        if (profile.Wallets.Count == 0)
        {
            throw new InvalidDataException("This vault holds no wallet.");
        }

        foreach (ContactDto c in dto.Contacts ?? [])
        {
            profile.Contacts.Add(new Contact
            {
                Id = string.IsNullOrEmpty(c.Id) ? WalletSecrets.NewId() : c.Id,
                Name = c.Name ?? string.Empty,
                Address = c.Address ?? string.Empty,
                Note = c.Note ?? string.Empty,
            });
        }

        return profile;
    }

    /// <summary>A v1/v2 slot: one wallet, which becomes a one-wallet profile.</summary>
    private static WalletProfile FromSingleWallet(ReadDto dto, int version)
    {
        var wallet = new WalletSecrets
        {
            Network = dto.Network,
            Mnemonic = dto.Mnemonic ?? string.Empty,
            SeedOffset = dto.SeedOffset ?? string.Empty,
            RestoreHeight = dto.RestoreHeight,
            DaemonAddress = dto.DaemonAddress ?? string.Empty,
            EphemeralWalletPassword = dto.EphemeralWalletPassword ?? string.Empty,
        };

        return new WalletProfile
        {
            Wallets = { wallet },
            ActiveWalletId = wallet.Id,
            // v1 only ever honoured the wipe flag on a duress-kind slot.
            WipeOtherSlotOnUnlock = version == Version01
                ? dto.LegacyKind == LegacyDuressKind && dto.LegacyDuressWipeReal == true
                : dto.WipeOther,
        };
    }

    private const int LegacyDuressKind = 1;

    /// <summary>A key image as wallet-rpc prints it: 64 lower-case hex digits. Anything else in the
    /// frozen list is dropped rather than sent to the backend.</summary>
    internal static bool IsKeyImage(string? s) =>
        s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>What v3 writes.</summary>
    private sealed class ProfileDto
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("wipeOther")] public bool WipeOther { get; set; }
        [JsonPropertyName("active")] public string? Active { get; set; }
        [JsonPropertyName("wallets")] public List<WalletDto>? Wallets { get; set; }
        [JsonPropertyName("contacts")] public List<ContactDto>? Contacts { get; set; }
    }

    /// <summary>What any version may contain: v3's profile fields and v1/v2's single-wallet fields.</summary>
    private sealed class ReadDto
    {
        [JsonPropertyName("v")] public int? Version { get; set; }
        [JsonPropertyName("wipeOther")] public bool WipeOther { get; set; }

        // ---- v3 ----
        [JsonPropertyName("active")] public string? Active { get; set; }
        [JsonPropertyName("wallets")] public List<WalletDto>? Wallets { get; set; }
        [JsonPropertyName("contacts")] public List<ContactDto>? Contacts { get; set; }

        // ---- v1/v2: one wallet ----
        [JsonPropertyName("network")] public MoneroNetwork Network { get; set; }
        [JsonPropertyName("mnemonic")] public string? Mnemonic { get; set; }
        [JsonPropertyName("seedOffset")] public string? SeedOffset { get; set; }
        [JsonPropertyName("restoreHeight")] public ulong RestoreHeight { get; set; }
        [JsonPropertyName("daemonAddress")] public string? DaemonAddress { get; set; }
        [JsonPropertyName("ephemeralWalletPassword")] public string? EphemeralWalletPassword { get; set; }

        // ---- v1 only: read for migration, never written ----
        [JsonPropertyName("kind")] public int? LegacyKind { get; set; }
        [JsonPropertyName("label")] public string? LegacyLabel { get; set; }
        [JsonPropertyName("duressWipeReal")] public bool? LegacyDuressWipeReal { get; set; }
    }

    private sealed class WalletDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        // "type", not "kind": 0.1 used a top-level "kind" as its real/decoy marker, and nothing in
        // the current format should even look like one.
        [JsonPropertyName("type")] public WalletKind Kind { get; set; }
        [JsonPropertyName("network")] public MoneroNetwork Network { get; set; }
        [JsonPropertyName("mnemonic")] public string? Mnemonic { get; set; }
        [JsonPropertyName("seedOffset")] public string? SeedOffset { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("viewKey")] public string? ViewKey { get; set; }
        [JsonPropertyName("spendKey")] public string? SpendKey { get; set; }
        [JsonPropertyName("restoreHeight")] public ulong RestoreHeight { get; set; }
        [JsonPropertyName("daemonAddress")] public string? DaemonAddress { get; set; }
        [JsonPropertyName("ephemeralWalletPassword")] public string? EphemeralWalletPassword { get; set; }
        [JsonPropertyName("subaddresses")] public Dictionary<uint, uint>? SubaddressCounts { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
        [JsonPropertyName("accountLabels")] public Dictionary<uint, string>? AccountLabels { get; set; }
        [JsonPropertyName("notes")] public Dictionary<string, string>? TxNotes { get; set; }

        // Always written (as [] when empty), so every slot has it whatever its wallets hold.
        [JsonPropertyName("frozen")] public List<string>? Frozen { get; set; }
    }

    private sealed class ContactDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
