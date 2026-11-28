using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace LegacyShop.Core.Services
{
    // Used to persist cart snapshots between requests. Classic .NET Framework pattern.
    public static class SnapshotSerializer
    {
        public static byte[] Serialize(object value)
        {
            using (var ms = new MemoryStream())
            {
                new BinaryFormatter().Serialize(ms, value);
                return ms.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                return (T)new BinaryFormatter().Deserialize(ms);
            }
        }
    }
}
