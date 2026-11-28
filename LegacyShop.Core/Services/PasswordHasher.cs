using System.Security.Cryptography;
using System.Text;

namespace LegacyShop.Core.Services
{
    // Typical legacy hashing. It compiles on every framework, but the bytes it hashes depend on the
    // server's code page on .NET Framework, and the string it returns depends on how invalid UTF-8 is decoded.
    public static class PasswordHasher
    {
        public static string Hash(string userName, string password)
        {
            using (var md5 = MD5.Create())
            {
                var input = Encoding.Default.GetBytes(userName.ToLower() + ":" + password);
                var hash = md5.ComputeHash(input);
                return Encoding.UTF8.GetString(hash);
            }
        }

        public static int CacheKey(string userName)
        {
            return userName.GetHashCode();
        }
    }
}
