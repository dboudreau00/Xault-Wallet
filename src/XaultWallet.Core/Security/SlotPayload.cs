using System.Text.Json;
using System.Text.Json.Serialization;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>
/// The JSON sealed inside a vault slot.
///
/// Version 2 (current) has ONE field set for every slot — there is no "real"/"decoy" marker and
/// no label — so the plaintext of a decoy slot is indistinguishable from the plaintext of the only
/// wallet in a single-wallet vault:
/// <code>{"v":2,"network":..,"mnemonic":..,"seedOffset":..,"restoreHeight":..,"daemonAddress":..,"ephemeralWalletPassword":..,"wipeOther":false}</code>
///
/// Version 1 (legacy, no "v" field) also carried <c>kind</c> (0 real / 1 duress), <c>label</c>
/// ("Main" vs "Wallet") and <c>duressWipeReal</c>. Anyone holding the vault file plus the duress
/// password — exactly the coercion scenario the feature exists for — could decrypt the decoy and
/// read <c>"kind":1</c>, proving a hidden wallet. v1 slots are still readable and are re-sealed
/// as v2 the first time they are opened.
/// </summary>
internal static class SlotPayload
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize to the v2 schema. The caller owns (and must zero) the returned bytes.</summary>
    public static byte[] Serialize(WalletSecrets s) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dto
        {
            Version = CurrentVersion,
            Network = s.Network,
            Mnemonic = s.Mnemonic,
            SeedOffset = s.SeedOffset,
            RestoreHeight = s.RestoreHeight,
            DaemonAddress = s.DaemonAddress,
            EphemeralWalletPassword = s.EphemeralWalletPassword,
            WipeOther = s.WipeOtherSlotOnUnlock,
        }, Json);

    /// <summary>
    /// Parse a slot payload of any supported version. <paramref name="isLegacy"/> is true for a
    /// v1 payload, which the caller should re-seal as v2.
    /// </summary>
    /// <exception cref="InvalidDataException">Unparseable, or written by a newer app version.</exception>
    public static WalletSecrets Deserialize(ReadOnlySpan<byte> data, out bool isLegacy)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(data, Json);
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

        isLegacy = dto.Version is null;
        return new WalletSecrets
        {
            Network = dto.Network,
            Mnemonic = dto.Mnemonic ?? string.Empty,
            SeedOffset = dto.SeedOffset ?? string.Empty,
            RestoreHeight = dto.RestoreHeight,
            DaemonAddress = dto.DaemonAddress ?? string.Empty,
            EphemeralWalletPassword = dto.EphemeralWalletPassword ?? string.Empty,
            // v1 only ever honoured the wipe flag on a duress-kind slot.
            WipeOtherSlotOnUnlock = isLegacy
                ? dto.LegacyKind == LegacyDuressKind && dto.LegacyDuressWipeReal == true
                : dto.WipeOther,
        };
    }

    private const int LegacyDuressKind = 1;

    private sealed class Dto
    {
        [JsonPropertyName("v")] public int? Version { get; set; }
        [JsonPropertyName("network")] public MoneroNetwork Network { get; set; }
        [JsonPropertyName("mnemonic")] public string? Mnemonic { get; set; }
        [JsonPropertyName("seedOffset")] public string? SeedOffset { get; set; }
        [JsonPropertyName("restoreHeight")] public ulong RestoreHeight { get; set; }
        [JsonPropertyName("daemonAddress")] public string? DaemonAddress { get; set; }
        [JsonPropertyName("ephemeralWalletPassword")] public string? EphemeralWalletPassword { get; set; }
        [JsonPropertyName("wipeOther")] public bool WipeOther { get; set; }

        // ---- v1 (legacy) fields: read for migration, never written (null => omitted) ----
        [JsonPropertyName("kind")] public int? LegacyKind { get; set; }
        [JsonPropertyName("label")] public string? LegacyLabel { get; set; }
        [JsonPropertyName("duressWipeReal")] public bool? LegacyDuressWipeReal { get; set; }
    }
}
