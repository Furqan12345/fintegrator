using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services.Security;

public class EncryptionService : IEncryptionService
{
    private const string DevelopmentFallbackKey = "SuperSecretEncryptionKey12345678";

    private readonly byte[] _key;

    public EncryptionService(IConfiguration configuration, IHostEnvironment environment, ILogger<EncryptionService> logger)
    {
        var configuredKey = configuration["Encryption:Key"];
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            _key = Encoding.UTF8.GetBytes(configuredKey);
            if (_key.Length != 32)
            {
                throw new InvalidOperationException("Encryption:Key must be exactly 32 bytes.");
            }
        }
        else if (environment.IsDevelopment())
        {
            logger.LogWarning("Encryption:Key is not configured; falling back to the built-in development key.");
            _key = Encoding.UTF8.GetBytes(DevelopmentFallbackKey);
        }
        else
        {
            throw new InvalidOperationException("Encryption:Key configuration is required outside Development.");
        }
    }

    public Task<string> EncryptAsync(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return Task.FromResult(string.Empty);

        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, nonce.Length + tag.Length, cipherBytes.Length);

        return Task.FromResult(Convert.ToBase64String(result));
    }

    public Task<string> DecryptAsync(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return Task.FromResult(string.Empty);

        var fullCipherBytes = Convert.FromBase64String(cipherText);

        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = new byte[nonceSize];
        var tag = new byte[tagSize];
        var cipherBytes = new byte[fullCipherBytes.Length - nonceSize - tagSize];

        Buffer.BlockCopy(fullCipherBytes, 0, nonce, 0, nonceSize);
        Buffer.BlockCopy(fullCipherBytes, nonceSize, tag, 0, tagSize);
        Buffer.BlockCopy(fullCipherBytes, nonceSize + tagSize, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, tagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Task.FromResult(Encoding.UTF8.GetString(plainBytes));
    }
}
