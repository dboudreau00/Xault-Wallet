using System.Buffers;
using System.Text;
using System.Text.Json;
using XaultWallet.Core.Monero;

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
            string u = (uri ?? string.Empty).Trim();
            if (u.Length == 0 || u.Length > MaxUriLength || !MoneroUri.IsUri(u) || HasControlCharacters(u))
            {
                return Fail(out error, "A payment request needs a monero: link (at most 4096 characters, no line breaks).");
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

    /// <summary>A reply from the app is passed on to the browser only if it is a JSON object of this
    /// version answering this request, and small.</summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> reply, string id)
    {
        if (reply.Length == 0 || reply.Length > MaxReplyBytes)
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(reply.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("v", out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int version) && version == Version
                && root.TryGetProperty("id", out JsonElement rid) && rid.ValueKind == JsonValueKind.String && rid.GetString() == id
                && root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>scheme://host[:port] of an http(s) page, with the host in its ASCII form (so a look-alike
    /// in another script shows as xn--… instead of passing for the real name); null when it isn't one.</summary>
    public static string? NormalizeOrigin(string? origin)
    {
        string o = (origin ?? string.Empty).Trim();
        if (o.Length == 0 || o.Length > MaxOriginLength || HasControlCharacters(o)
            || !Uri.TryCreate(o, UriKind.Absolute, out Uri? u)
            || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
            || u.IdnHost.Length == 0 || u.UserInfo.Length > 0)
        {
            return null;
        }

        return u.IsDefaultPort ? $"{u.Scheme}://{u.IdnHost}" : $"{u.Scheme}://{u.IdnHost}:{u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>The host part of a normalized origin, for showing ("shop.example").</summary>
    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? u) ? u.IdnHost : string.Empty;

    /// <summary>A label as it will be stored: trimmed, control characters dropped, inner whitespace
    /// collapsed; null when still too long.</summary>
    public static string? CleanLabel(string? label)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char c in (label ?? string.Empty).Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true; // tabs and line breaks too: one space between words
                continue;
            }

            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format)
            {
                continue; // includes the bidirectional overrides that can make text read backwards
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.Length <= MaxLabelLength ? sb.ToString() : null;
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

    private static bool HasControlCharacters(string s)
    {
        foreach (char c in s)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>The user is in the middle of something (a payment being confirmed).</summary>
    public const string Busy = "busy";

    /// <summary>Too many requests of this kind in a short time.</summary>
    public const string TooMany = "too-many";

    /// <summary>The app couldn't do it (the message says why).</summary>
    public const string Failed = "failed";
}
