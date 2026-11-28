using System.IO;
using System.Text.Json;

namespace LegacyShop.Core.Services
{
    // Used to persist cart snapshots between requests. Classic .NET Framework pattern.
    public static class SnapshotSerializer
    {
        public static byte[] Serialize(object value)
        {
            using (var ms = new MemoryStream())
            {
                JsonSerializer.Serialize(ms, value);
                return ms.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                return JsonSerializer.Deserialize<T>(ms);
            }
        }
    }
}
