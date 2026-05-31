using Konscious.Security.Cryptography;
using System.Security.Cryptography;
using System.Text;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public class PasswordHasher : IPasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int DegreeOfParallelism = 1;
        private const int Iterations = 2;
        private const int MemorySize = 19 * 1024;

        public (byte[] Salt, byte[] Hash) HashPassword(string password)
        {
            var salt = new byte[SaltSize];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(salt);

            var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = DegreeOfParallelism,
                Iterations = Iterations,
                MemorySize = MemorySize
            };
            var hash = argon2.GetBytes(HashSize);
            return (salt, hash);
        }

        public bool VerifyPassword(string password, byte[] salt, byte[] hash)
        {
            var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = DegreeOfParallelism,
                Iterations = Iterations,
                MemorySize = MemorySize
            };
            var computed = argon2.GetBytes(hash.Length);
            return CryptographicOperations.FixedTimeEquals(computed, hash);
        }
    }
}
