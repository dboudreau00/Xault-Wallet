using System.Buffers;
using System.Text.Json;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Companion;

/// <summary>What a request asks the running app to do.</summary>
public enum CompanionRequestType
{
    /// <summary>Is the app running, is the vault unlocked, which wallet is on screen.</summary>
    Status,

    /// <summary>Bring the window forward.</summary>
    Show,

    /// <summary>Open a monero: payment request in the Send tab, for the user to review and confirm.</summary>
    Pay,

    /// <summary>A fresh subaddress of the wallet on screen, for the user to paste somewhere.</summary>
    Receive,
}

/// <summary>Who passed the request on: the companion extension in a browser (through the native
/// messaging host), or a monero: link the operating system handed to a second launch of the app.</summary>
public enum CompanionCaller
{
    Chrome,
    Firefox,
    Link,
}

/// <summary>One validated request. Strings are empty when absent.</summary>
public sealed record CompanionRequest(string Id, CompanionRequestType Type, CompanionCaller Via, string Uri = "", string Origin = "", string Label = "");

/// <summary>Why a request was refused: a short code for the extension to act on, and a sentence to show.</summary>
public sealed record CompanionError(string Code, string Message);

/// <summary>
/// The messages between the browser companion and the running app, version 1. One JSON object per
/// request and per reply; the browser frames them with a length prefix (see <see cref="NativeMessaging"/>),
/// the local channel to the app with a newline (see <see cref="CompanionWire"/>). Every field is checked
/// here, by the native host and again by the app: neither trusts the other end.
/// <para>
/// The companion never receives keys, seeds, balances or history. It can ask for the app's state, bring
/// the window forward, put a payment request in front of the user (who still reviews and confirms it in
/// the app), and get a new receive address.
/// </para>
/// </summary>
public static class CompanionProtocol
{
    public const int Version = 1;

    /// <summary>Largest request accepted, in bytes of JSON. A payment link is well under 1 KB.</summary>
    public const int MaxRequestBytes = 16 * 1024;

    /// <summary>Largest reply passed back to the browser, in bytes of JSON.</summary>
    public const int MaxReplyBytes = 16 * 1024;

    public const int MaxIdLength = 64;
    public const int MaxUriLength = 4096;
    public const int MaxOriginLength = 512;

    /// <summary>The same limit as a label typed on the Receive tab.</summary>
    public const int MaxLabelLength = 40;

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Parse and check a request. <paramref name="via"/> is who passed it on, as the caller knows it
    /// (the native host from the command line the browser started it with); when it is null the request
    /// itself must say (the local channel, where the host or a link launch fills it in). A "via" field
    /// in a request from a browser is ignored: the extension doesn't get to say who it is.
    /// </summary>
    public static CompanionRequest? Parse(ReadOnlySpan<byte> utf8Json, CompanionCaller? via, out CompanionError? error)
    {
        error = null;
        if (utf8Json.Length > MaxRequestBytes)
        {
            error = new CompanionError(CompanionErrors.TooLarge, "The request is too large.");
            return null;
        }

        int? version = null;
        string? id = null, type = null, viaText = null, uri = null, origin = null, label = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(utf8Json, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return Fail(out error, "The request isn't a JSON object.");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string name = reader.GetString() ?? string.Empty;
                if (!seen.Add(name))
                {
                    return Fail(out error, "The request has the field \"" + Shorten(name) + "\" twice.");
                }

                if (!reader.Read())
                {
                    return Fail(out error, "The request is cut short.");
                }

                switch (name)
                {
                    case "v":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int v))
                        {
                            return Fail(out error, "\"v\" must be a whole number.");
                        }

                        version = v;
                        break;
                    case "id":
                        id = ReadString(ref reader);
                        break;
                    case "type":
                        type = ReadString(ref reader);
                        break;
                    case "via":
                        viaText = ReadString(ref reader);
                        break;
                    case "uri":
                        uri = ReadString(ref reader);
                        break;
                    case "origin":
                        origin = ReadString(ref reader);
                        break;
                    case "label":
                        label = ReadString(ref reader);
                        break;
                    default:
                        reader.Skip(); // a later version's field: ignored, and the whole request is size-capped
                        break;
                }
            }

            if (reader.Read())
            {
                return Fail(out error, "The request has something after its closing brace.");
            }
        }
        catch (JsonException)
        {
            return Fail(out error, "The request isn't valid JSON.");
        }
        catch (InvalidOperationException)
        {
            // A field of the wrong kind (a number where text belongs, say).
            return Fail(out error, "A field of the request has the wrong type.");
        }

        if (version != Version)
        {
            error = new CompanionError(CompanionErrors.UnsupportedVersion,
                version is null ? "The request has no version (\"v\")." : $"This XaultWallet speaks version {Version} of the companion protocol, not {version}. Update the app or the extension.");
            return null;
        }

        if (id is null || !IsValidId(id))
        {
            return Fail(out error, "\"id\" must be 1 to 64 letters, digits, '-' or '_'.");
        }

        CompanionRequestType? kind = type switch
        {
            "status" => CompanionRequestType.Status,
            "show" => CompanionRequestType.Show,
            "pay" => CompanionRequestType.Pay,
            "receive" => CompanionRequestType.Receive,
            _ => null,
        };
        if (kind is not { } requestType)
        {
            error = new CompanionError(CompanionErrors.UnknownType, "Unknown request type \"" + Shorten(type ?? string.Empty) + "\".");
            return null;
        }

        CompanionCaller caller;
        if (via is { } given)
        {
            caller = given;
        }
        else if (viaText switch { "chrome" => CompanionCaller.Chrome, "firefox" => CompanionCaller.Firefox, "link" => CompanionCaller.Link, _ => (CompanionCaller?)null } is { } said)
        {
            caller = said;
        }
        else
        {
            return Fail(out error, "The request doesn't say who passed it on (\"via\").");
        }

        string cleanUri = string.Empty, cleanOrigin = string.Empty, cleanLabel = string.Empty;
        if (requestType == CompanionRequestType.Pay)
        {
            if (CleanPaymentUri(uri) is not { } u)
            {
                return Fail(out error, "A payment request needs a monero: link (at most 4096 characters, no line breaks or hidden characters).");
            }

            cleanUri = u;
        }

        if (origin is { Length: > 0 })
        {
            if (NormalizeOrigin(origin) is not { } o)
            {
                return Fail(out error, "\"origin\" must be the http or https address of the page.");
            }

            cleanOrigin = o;
        }

        if (requestType == CompanionRequestType.Receive && label is not null)
        {
            if (CleanLabel(label) is not { } l)
            {
                return Fail(out error, $"Keep the label under {MaxLabelLength} characters.");
            }

            cleanLabel = l;
        }

        return new CompanionRequest(id, requestType, caller, cleanUri, cleanOrigin, cleanLabel);
    }

    /// <summary>The id of a request that failed to parse, when one can still be read (so the error
    /// reply can be matched to it); empty otherwise.</summary>
    public static string TryReadId(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return string.Empty;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isId = reader.ValueTextEquals("id");
                reader.Read();
                if (isId && reader.TokenType == JsonTokenType.String && reader.GetString() is { } id && IsValidId(id))
                {
                    return id;
                }

                reader.Skip();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // fall through: no usable id
        }

        return string.Empty;
    }

    /// <summary>The request as the local channel carries it: the native host's own copy, with the
    /// caller it established filled in.</summary>
    public static byte[] Serialize(CompanionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", request.Id);
            w.WriteString("type", request.Type switch
            {
                CompanionRequestType.Status => "status",
                CompanionRequestType.Show => "show",
                CompanionRequestType.Pay => "pay",
                _ => "receive",
            });
            w.WriteString("via", ViaName(request.Via));
            if (request.Uri.Length > 0)
            {
                w.WriteString("uri", request.Uri);
            }

            if (request.Origin.Length > 0)
            {
                w.WriteString("origin", request.Origin);
            }

            if (request.Label.Length > 0)
            {
                w.WriteString("label", request.Label);
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string ViaName(CompanionCaller via) => via switch
    {
        CompanionCaller.Chrome => "chrome",
        CompanionCaller.Firefox => "firefox",
        _ => "link",
    };

    /// <summary>A successful reply; <paramref name="body"/> adds the fields of this kind of request.</summary>
    public static byte[] Ok(string id, Action<Utf8JsonWriter>? body = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id ?? string.Empty);
            w.WriteBoolean("ok", true);
            body?.Invoke(w);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A refusal: <c>{"ok": false, "error": {"code", "message"}}</c>.</summary>
    public static byte[] Fail(string id, CompanionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id ?? string.Empty);
            w.WriteBoolean("ok", false);
            w.WriteStartObject("error");
            w.WriteString("code", error.Code);
            w.WriteString("message", error.Message);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The app's reply to <paramref name="request"/> as the browser gets it: rebuilt from the fields this
    /// kind of request answers with, each one checked, so nothing else a program on the local channel
    /// might add reaches the extension. Null when it isn't a usable answer to this request.
    /// </summary>
    public static byte[]? RebuildReply(ReadOnlySpan<byte> reply, CompanionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (reply.Length == 0 || reply.Length > MaxReplyBytes)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(reply.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version) || version != Version
                || TextOf(root, "id") != request.Id
                || !root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (ok.ValueKind == JsonValueKind.False)
            {
                if (!root.TryGetProperty("error", out JsonElement e) || e.ValueKind != JsonValueKind.Object
                    || TextOf(e, "code") is not { } code || !CompanionErrors.All.Contains(code))
                {
                    return null;
                }

                string message = UntrustedText.Clean(TextOf(e, "message"), MaxMessageLength);
                return Fail(request.Id, new CompanionError(code, message.Length > 0 ? message : "XaultWallet couldn't do that."));
            }

            switch (request.Type)
            {
                case CompanionRequestType.Status:
                    string? state = TextOf(root, "state");
                    string? network = TextOf(root, "network");
                    string? appVersion = TextOf(root, "version");
                    if (state is not ("unlocked" or "locked" or "setup") || (network is not null && NetworkOf(network) is null))
                    {
                        return null;
                    }

                    return Ok(request.Id, w =>
                    {
                        w.WriteString("app", "XaultWallet");
                        if (appVersion is { Length: > 0 and <= 32 } && appVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
                        {
                            w.WriteString("version", appVersion);
                        }

                        w.WriteString("state", state);
                        if (network is not null)
                        {
                            w.WriteString("network", network);
                            w.WriteBoolean("watchOnly", root.TryGetProperty("watchOnly", out JsonElement wo) && wo.ValueKind == JsonValueKind.True);
                            w.WriteBoolean("ready", root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True);
                        }
                    });

                case CompanionRequestType.Show:
                    return Ok(request.Id);

                case CompanionRequestType.Pay:
                    string? result = TextOf(root, "result");
                    return result is "shown" or "after-unlock" or "waiting" or "held" ? Ok(request.Id, w => w.WriteString("result", result)) : null;

                default:
                    string? address = TextOf(root, "address");
                    string? addressNetwork = TextOf(root, "network");
                    return address is { Length: 95 } && NetworkOf(addressNetwork) is { } kind && MoneroAddress.Problem(address, kind) is null
                        ? Ok(request.Id, w =>
                        {
                            w.WriteString("address", address);
                            w.WriteString("network", addressNetwork);
                        })
                        : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null; // not JSON, or text that isn't valid UTF-8
        }
    }

    /// <summary>The longest refusal message passed on to the browser.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>The networks a reply may name, and the address format each uses (a private test chain
    /// uses mainnet's).</summary>
    private static Models.MoneroNetwork? NetworkOf(string? name) => name switch
    {
        "Mainnet" or "Regtest" => Models.MoneroNetwork.Mainnet,
        "Stagenet" => Models.MoneroNetwork.Stagenet,
        "Testnet" => Models.MoneroNetwork.Testnet,
        _ => null,
    };

    private static string? TextOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The checked form of a monero: link from outside (the browser, or the link a launch was
    /// given): trimmed, at most <see cref="MaxUriLength"/> characters, nothing hidden in it; null when
    /// it isn't one.</summary>
    public static string? CleanPaymentUri(string? uri)
    {
        string u = (uri ?? string.Empty).Trim();
        return u.Length > 0 && u.Length <= MaxUriLength && MoneroUri.IsUri(u) && !UntrustedText.HasHiddenCharacters(u) ? u : null;
    }

    /// <summary>scheme://host[:port] of an http(s) page, with the host in its ASCII form (so a look-alike
    /// in another script shows as xn--… instead of passing for the real name) and an IPv6 address in
    /// brackets; null when it isn't one.</summary>
    public static string? NormalizeOrigin(string? origin)
    {
        string o = (origin ?? string.Empty).Trim();
        if (o.Length == 0 || o.Length > MaxOriginLength || UntrustedText.HasHiddenCharacters(o)
            || !Uri.TryCreate(o, UriKind.Absolute, out Uri? u)
            || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
            || u.IdnHost.Length == 0 || u.UserInfo.Length > 0 || u.Port <= 0)
        {
            return null;
        }

        string host = HostPart(u);
        return u.IsDefaultPort ? $"{u.Scheme}://{host}" : $"{u.Scheme}://{host}:{u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>The host part of a normalized origin, for showing ("shop.example", "[::1]").</summary>
    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? u) ? HostPart(u) : string.Empty;

    /// <summary>True when <paramref name="origin"/> is a plain http page that isn't on this computer:
    /// anyone on the network path could have changed what it showed.</summary>
    public static bool IsInsecureOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback;

    private static string HostPart(Uri u) => u.HostNameType == UriHostNameType.IPv6 ? "[" + u.IdnHost + "]" : u.IdnHost;

    /// <summary>A label as it will be stored: hidden characters dropped (see <see cref="UntrustedText"/>),
    /// whitespace collapsed, trimmed; null when still too long.</summary>
    public static string? CleanLabel(string? label)
    {
        string cleaned = UntrustedText.Clean(label, int.MaxValue);
        return cleaned.Length <= MaxLabelLength ? cleaned : null;
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is 0 or > MaxIdLength)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadString(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new InvalidOperationException("not a string"),
        };

    private static string Shorten(string s) => s.Length > 24 ? s[..24] + "..." : s;

    private static CompanionRequest? Fail(out CompanionError? error, string message)
    {
        error = new CompanionError(CompanionErrors.BadRequest, message);
        return null;
    }
}

/// <summary>The error codes of the companion protocol (the "code" of a refusal).</summary>
public static class CompanionErrors
{
    /// <summary>The request is malformed or a field is invalid.</summary>
    public const string BadRequest = "bad-request";

    public const string TooLarge = "too-large";

    public const string UnsupportedVersion = "unsupported-version";

    public const string UnknownType = "unknown-type";

    /// <summary>XaultWallet isn't running (and wasn't started for this request).</summary>
    public const string NotRunning = "not-running";

    /// <summary>There is no vault yet: XaultWallet has to be set up first.</summary>
    public const string NoVault = "no-vault";

    /// <summary>The vault is locked.</summary>
    public const string Locked = "locked";

    /// <summary>The wallet on screen is still starting.</summary>
    public const string NotReady = "not-ready";

    /// <summary>The user is in the middle of something (a payment being confirmed, a form they started,
    /// a payment to check): the request changed nothing.</summary>
    public const string Busy = "busy";

    /// <summary>The wallet on screen is watch-only: it can't pay.</summary>
    public const string WatchOnly = "watch-only";

    /// <summary>Too many requests of this kind in a short time.</summary>
    public const string TooMany = "too-many";

    /// <summary>The app couldn't do it (the message says why).</summary>
    public const string Failed = "failed";

    /// <summary>The user hasn't connected this browser in XaultWallet's Settings (or refused its extension).</summary>
    public const string NotConnected = "not-connected";

    /// <summary>Every code a reply may carry: the native host passes no other on.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BadRequest, TooLarge, UnsupportedVersion, UnknownType, NotRunning, NoVault, Locked, NotReady, Busy, WatchOnly, TooMany, Failed, NotConnected,
    };
}
