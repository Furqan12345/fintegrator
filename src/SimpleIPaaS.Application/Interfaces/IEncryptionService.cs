using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public interface IEncryptionService
{
    Task<string> EncryptAsync(string plainText);
    Task<string> DecryptAsync(string cipherText);
}
