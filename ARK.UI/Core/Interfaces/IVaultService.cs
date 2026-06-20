namespace ARK.UI.Core.Interfaces;

public interface IVaultService
{
    Task<string> EncryptAsync(string clearText, CancellationToken cancellationToken = default);
    Task<string> DecryptAsync(string cipherText, CancellationToken cancellationToken = default);
}
