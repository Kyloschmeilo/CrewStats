using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CrewStats;

/// <summary>
/// Verschlüsselung der Rollenwunsch-Nachrichten im Discord-Channel: AES-256-GCM, Schlüssel = SHA-256 von
/// "crewstats-wishes:" + Webhook-Token, die Nachrichtenart als Zusatzdaten. So sieht im Channel niemand,
/// wer sich welche Rolle wünscht – nur der Bot und Mods mit der Webhook-URL können es lesen.
/// Format ↔ Bot: services/RoleWishes.kt (WishProtocol) – beide Seiten gemeinsam ändern!
/// </summary>
internal static class WishCrypto
{
    public const string Request = "wishes";
    public const string Reply = "wishes-reply";
    public const string Result = "wishes-result";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Der Token ist der letzte Teil der Webhook-URL (…/api/webhooks/&lt;id&gt;/&lt;token&gt;).</summary>
    public static string? TokenOf(string webhookUrl)
    {
        var path = webhookUrl.Split('?', '#')[0].TrimEnd('/');
        var parts = path.Split('/');
        return parts.Length >= 2 && parts[^2].Length > 0 && parts[^1].Length > 0 ? parts[^1] : null;
    }

    /// <summary>Nachrichtentext {"crewstats":kind,"v":1,"data":…} mit verschlüsseltem JSON.</summary>
    public static string Seal(string kind, string plaintext, string token)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(Key(token)))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(kind));

        var data = new byte[NonceBytes + cipher.Length + TagBytes]; // wie Java: Nonce | Chiffretext | Tag
        nonce.CopyTo(data, 0);
        cipher.CopyTo(data, NonceBytes);
        tag.CopyTo(data, NonceBytes + cipher.Length);
        return JsonSerializer.Serialize(new { crewstats = kind, v = 1, data = Convert.ToBase64String(data) }, Compact);
    }

    /// <summary>Entschlüsselt eine Nachricht der erwarteten Art – sonst (noch nicht beantwortet, kaputt) null.</summary>
    public static string? Open(string kind, string? content, string token)
    {
        if (string.IsNullOrEmpty(content)) return null;
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("crewstats", out var marker) || marker.GetString() != kind) return null;
            if (!root.TryGetProperty("data", out var dataElement)) return null;
            var data = Convert.FromBase64String(dataElement.GetString() ?? "");
            if (data.Length < NonceBytes + TagBytes) return null;

            var plain = new byte[data.Length - NonceBytes - TagBytes];
            using (var aes = new AesGcm(Key(token)))
            {
                aes.Decrypt(
                    data.AsSpan(0, NonceBytes),
                    data.AsSpan(NonceBytes, plain.Length),
                    data.AsSpan(NonceBytes + plain.Length, TagBytes),
                    plain,
                    Encoding.UTF8.GetBytes(kind));
            }
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exc) when (exc is JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Key(string token) => SHA256.HashData(Encoding.UTF8.GetBytes("crewstats-wishes:" + token));
}
