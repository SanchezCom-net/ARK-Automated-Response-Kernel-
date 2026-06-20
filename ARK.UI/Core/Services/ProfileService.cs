using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class ProfileService : IProfileService
{
    private const string Component = "ProfileService";

    private static readonly string[] SensitiveEndings = ["Secret", "Password", "ApiKey"];

    private readonly IVaultService   _vault;
    private readonly ILogService     _logger;
    private readonly IConfigService  _configService;
    private readonly SemaphoreSlim   _writeLock = new(1, 1);
    private readonly string          _profilesDir;
    private readonly JsonSerializerOptions _jsonOptions;

    // Profiles — это тот же экземпляр, что и AppConfig.Profiles.
    // MacroScheduler использует _configService.Current.Profiles напрямую — никакой дополнительной синхронизации не нужно.
    public ObservableCollection<AppProfile> Profiles => _configService.Current.Profiles;

    public ProfileService(IVaultService vault, ILogService logger, IConfigService configService)
    {
        _vault         = vault;
        _logger        = logger;
        _configService = configService;
        _profilesDir   = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "Profiles");
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented                   = true,
            PropertyNameCaseInsensitive     = true,
            // В .NET 8+ дефолтный режим Replace пропускает read-only коллекции (нет setter).
            // Populate гарантирует наполнение существующих ObservableCollection<T> при загрузке.
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };
    }

    public async Task LoadAllProfilesAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_profilesDir);

        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var raw     = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var root    = JsonNode.Parse(raw)?.AsObject();
                if (root is null) continue;

                await DecryptJsonObjectAsync(root, ct).ConfigureAwait(false);

                var profile = JsonSerializer.Deserialize<AppProfile>(root.ToJsonString(), _jsonOptions);
                if (profile is not null)
                {
                    RebuildAllVoiceKeywords(profile);
                    Profiles.Add(profile);
                }
            }
            catch (Exception ex)
            {
                await _logger.LogErrorAsync(Component,
                    $"Ошибка загрузки профиля: {Path.GetFileName(file)}", ex).ConfigureAwait(false);
            }
        }

        await _logger.LogInfoAsync(Component,
            $"Загружено профилей: {Profiles.Count}.").ConfigureAwait(false);
    }

    public async Task SaveProfileAsync(AppProfile profile, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_profilesDir);

            var json = JsonSerializer.Serialize(profile, _jsonOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            await EncryptJsonObjectAsync(root, ct).ConfigureAwait(false);

            var finalJson  = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var finalPath  = Path.Combine(_profilesDir, $"{profile.Id}.json");
            var tempPath   = finalPath + ".tmp";

            // Safe-Write: пишем во временный файл, затем атомарно заменяем.
            // Гарантирует, что оригинал не будет повреждён при внезапном завершении.
            await File.WriteAllTextAsync(tempPath, finalJson, ct).ConfigureAwait(false);

            if (File.Exists(finalPath))
                File.Replace(tempPath, finalPath, null);
            else
                File.Move(tempPath, finalPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteProfileAsync(AppProfile profile, CancellationToken ct = default)
    {
        Profiles.Remove(profile);
        var path = Path.Combine(_profilesDir, $"{profile.Id}.json");
        if (File.Exists(path))
            File.Delete(path);
        await _logger.LogInfoAsync(Component,
            $"Профиль удалён: {profile.FriendlyName} ({profile.Id}).").ConfigureAwait(false);
    }

    // ── Шифрование / расшифровка JSON-дерева ──────────────────────────────

    private async Task EncryptJsonObjectAsync(JsonObject obj, CancellationToken ct)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (obj[key] is JsonValue val
                && val.TryGetValue<string>(out var str)
                && IsSensitiveKey(key))
            {
                obj[key] = await _vault.EncryptAsync(str, ct).ConfigureAwait(false);
            }
            else if (obj[key] is JsonObject nested)
            {
                await EncryptJsonObjectAsync(nested, ct).ConfigureAwait(false);
            }
            else if (obj[key] is JsonArray arr)
            {
                await EncryptJsonArrayAsync(arr, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task EncryptJsonArrayAsync(JsonArray arr, CancellationToken ct)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject obj)
                await EncryptJsonObjectAsync(obj, ct).ConfigureAwait(false);
            else if (arr[i] is JsonArray nested)
                await EncryptJsonArrayAsync(nested, ct).ConfigureAwait(false);
        }
    }

    private async Task DecryptJsonObjectAsync(JsonObject obj, CancellationToken ct)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (obj[key] is JsonValue val
                && val.TryGetValue<string>(out var cipher)
                && IsSensitiveKey(key)
                && !string.IsNullOrEmpty(cipher))
            {
                try
                {
                    obj[key] = await _vault.DecryptAsync(cipher, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Поле уже в открытом виде (напр. при первом запуске без шифрования) — оставляем как есть
                }
            }
            else if (obj[key] is JsonObject nested)
            {
                await DecryptJsonObjectAsync(nested, ct).ConfigureAwait(false);
            }
            else if (obj[key] is JsonArray arr)
            {
                await DecryptJsonArrayAsync(arr, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DecryptJsonArrayAsync(JsonArray arr, CancellationToken ct)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject obj)
                await DecryptJsonObjectAsync(obj, ct).ConfigureAwait(false);
            else if (arr[i] is JsonArray nested)
                await DecryptJsonArrayAsync(nested, ct).ConfigureAwait(false);
        }
    }

    private static bool IsSensitiveKey(string key)
        => SensitiveEndings.Any(e => key.EndsWith(e, StringComparison.Ordinal));

    // ── Индекс голосовых ключевых слов ───────────────────────────────────

    private static void RebuildAllVoiceKeywords(AppProfile profile)
    {
        foreach (var m in FlattenMacros(profile))
            m.RebuildVoiceKeywordsIndex();
    }

    private static IEnumerable<MacroEntry> FlattenMacros(AppProfile profile)
    {
        foreach (var m in profile.Macros)  yield return m;
        foreach (var r in profile.Regions) foreach (var m in r.Macros) yield return m;
        foreach (var f in profile.Folders) foreach (var m in FlattenMacros(f)) yield return m;
    }

    private static IEnumerable<MacroEntry> FlattenMacros(VisualFolder folder)
    {
        foreach (var m in folder.Macros)     yield return m;
        foreach (var r in folder.Regions)    foreach (var m in r.Macros)        yield return m;
        foreach (var f in folder.SubFolders) foreach (var m in FlattenMacros(f)) yield return m;
    }

    // ── Экспорт / Импорт (.ark) ───────────────────────────────────────────

    private const string ArkEncPrefix   = "__ARK1:";
    private const string ArkSaltRootKey = "__ark_export_salt";
    private const int    PbkdfIterations = 100_000;
    private const int    NonceSize       = 12;
    private const int    TagSize         = 16;
    private const int    KeySize         = 32;
    private const int    SaltSize        = 16;

    public async Task ExportProfileAsync(
        AppProfile profile, string targetFilePath, string? password, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(profile, _jsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        byte[]? key  = null;
        byte[]? salt = null;
        try
        {
            if (password is not null)
            {
                salt = RandomNumberGenerator.GetBytes(SaltSize);
                key  = Rfc2898DeriveBytes.Pbkdf2(
                    password.AsSpan(), salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);

                EncryptJsonObjectAesGcm(root, key);
                root[ArkSaltRootKey] = Convert.ToBase64String(salt);
            }
            else
            {
                await EncryptJsonObjectAsync(root, ct).ConfigureAwait(false);
            }

            var jsonBytes = Encoding.UTF8.GetBytes(
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            await using var fs = new FileStream(
                targetFilePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true);
            using var zip   = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
            var       entry = zip.CreateEntry($"{profile.Id}.json", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            if (key  is not null) CryptographicOperations.ZeroMemory(key);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
        }

        await _logger.LogInfoAsync(Component,
            $"Профиль '{profile.FriendlyName}' экспортирован: {Path.GetFileName(targetFilePath)}.")
            .ConfigureAwait(false);
    }

    public async Task<AppProfile> ImportProfileAsync(
        string sourceFilePath, string? password, CancellationToken ct = default)
    {
        string json;
        await using (var fs = new FileStream(
            sourceFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: true))
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(
                            e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("Пакет ARK не содержит файл профиля.");

            using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            await using var es = entry.Open();
            await es.CopyToAsync(ms, ct).ConfigureAwait(false);
            json = Encoding.UTF8.GetString(ms.ToArray());
        }

        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidDataException("Повреждённый JSON в пакете ARK.");

        bool hasAesGcm = root[ArkSaltRootKey] is JsonValue;
        if (hasAesGcm)
        {
            if (string.IsNullOrEmpty(password))
                throw new CryptographicException("Профиль защищён паролем. Требуется пароль для импорта.");

            byte[]? key  = null;
            byte[]? salt = null;
            try
            {
                salt = Convert.FromBase64String(root[ArkSaltRootKey]!.GetValue<string>());
                key  = Rfc2898DeriveBytes.Pbkdf2(
                    password.AsSpan(), salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);

                DecryptJsonObjectAesGcm(root, key); // throws CryptographicException on wrong password
                root.Remove(ArkSaltRootKey);
            }
            finally
            {
                if (key  is not null) CryptographicOperations.ZeroMemory(key);
                if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            }
        }
        else
        {
            await DecryptJsonObjectAsync(root, ct).ConfigureAwait(false);
        }

        var profile = JsonSerializer.Deserialize<AppProfile>(root.ToJsonString(), _jsonOptions)
                      ?? throw new InvalidDataException("Не удалось десериализовать профиль из пакета ARK.");

        RebuildAllVoiceKeywords(profile);
        await SaveProfileAsync(profile, ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(Component,
            $"Профиль '{profile.FriendlyName}' импортирован из: {Path.GetFileName(sourceFilePath)}.")
            .ConfigureAwait(false);

        return profile;
    }

    // ── AES-256-GCM: обход JSON-дерева ────────────────────────────────────

    private static void EncryptJsonObjectAesGcm(JsonObject obj, byte[] key)
    {
        foreach (var k in obj.Select(kv => kv.Key).ToList())
        {
            if (obj[k] is JsonValue val && val.TryGetValue<string>(out var str) && IsSensitiveKey(k))
                obj[k] = EncryptStringAesGcm(str, key);
            else if (obj[k] is JsonObject nested) EncryptJsonObjectAesGcm(nested, key);
            else if (obj[k] is JsonArray  arr)    EncryptJsonArrayAesGcm(arr, key);
        }
    }

    private static void EncryptJsonArrayAesGcm(JsonArray arr, byte[] key)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            if      (arr[i] is JsonObject obj)    EncryptJsonObjectAesGcm(obj, key);
            else if (arr[i] is JsonArray  nested) EncryptJsonArrayAesGcm(nested, key);
        }
    }

    private static void DecryptJsonObjectAesGcm(JsonObject obj, byte[] key)
    {
        foreach (var k in obj.Select(kv => kv.Key).ToList())
        {
            if (obj[k] is JsonValue val
                && val.TryGetValue<string>(out var cipher)
                && IsSensitiveKey(k)
                && cipher.StartsWith(ArkEncPrefix, StringComparison.Ordinal))
            {
                obj[k] = DecryptStringAesGcm(cipher, key);
            }
            else if (obj[k] is JsonObject nested) DecryptJsonObjectAesGcm(nested, key);
            else if (obj[k] is JsonArray  arr)    DecryptJsonArrayAesGcm(arr, key);
        }
    }

    private static void DecryptJsonArrayAesGcm(JsonArray arr, byte[] key)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            if      (arr[i] is JsonObject obj)    DecryptJsonObjectAesGcm(obj, key);
            else if (arr[i] is JsonArray  nested) DecryptJsonArrayAesGcm(nested, key);
        }
    }

    // Формат: ArkEncPrefix + Base64( nonce[12] || tag[16] || ciphertext[N] )
    private static string EncryptStringAesGcm(string plaintext, byte[] key)
    {
        var plainBytes  = Encoding.UTF8.GetBytes(plaintext);
        var nonce       = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag         = new byte[TagSize];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

            var blob = new byte[NonceSize + TagSize + cipherBytes.Length];
            Buffer.BlockCopy(nonce,       0, blob, 0,                       NonceSize);
            Buffer.BlockCopy(tag,         0, blob, NonceSize,               TagSize);
            Buffer.BlockCopy(cipherBytes, 0, blob, NonceSize + TagSize,     cipherBytes.Length);
            return ArkEncPrefix + Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(cipherBytes);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static string DecryptStringAesGcm(string encrypted, byte[] key)
    {
        var blob        = Convert.FromBase64String(encrypted[ArkEncPrefix.Length..]);
        var nonce       = blob.AsSpan(0, NonceSize);
        var tag         = blob.AsSpan(NonceSize, TagSize);
        var cipherBytes = blob.AsSpan(NonceSize + TagSize);
        var plainBytes  = new byte[cipherBytes.Length];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(blob);
        }
    }
}
