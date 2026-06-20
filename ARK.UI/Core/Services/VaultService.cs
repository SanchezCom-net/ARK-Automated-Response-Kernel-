using System.Security.Cryptography;
using System.Text;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

public sealed class VaultService : IVaultService
{
    private readonly ILogService _logger;

    // Энтропия (соль) — "ARK-Vault" в UTF-8. Не содержит пользовательских данных.
    private static readonly byte[] Entropy =
        [0x41, 0x52, 0x4B, 0x2D, 0x56, 0x61, 0x75, 0x6C, 0x74];

    public VaultService(ILogService logger)
    {
        _logger = logger;
    }

    public Task<string> EncryptAsync(string clearText, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                byte[] plainBytes  = Encoding.UTF8.GetBytes(clearText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy,
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
            catch (Exception ex)
            {
                _ = _logger.LogErrorAsync(nameof(VaultService), "Ошибка шифрования DPAPI.", ex);
                throw;
            }
        }, cancellationToken);

    public Task<string> DecryptAsync(string cipherText, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                byte[] plainBytes  = ProtectedData.Unprotect(cipherBytes, Entropy,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                _ = _logger.LogErrorAsync(nameof(VaultService), "Ошибка дешифрования DPAPI.", ex);
                throw;
            }
        }, cancellationToken);
}
