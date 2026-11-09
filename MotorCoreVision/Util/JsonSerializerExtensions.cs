using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FAVIS
{
    public static class Json
    {
        public static T Deserialize<T>(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return Deserialize<T>(json, null);
        }

        public static T Deserialize<T>(string json, Func<string, string> normalize)
        {
            if (normalize != null)
            {
                json = normalize(json);
            }

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }

        public static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static void Serialize<T>(string path, T value)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                {
                    serializer.WriteObject(writer, value);
                    writer.Flush();
                }
                File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()), new UTF8Encoding(false));
            }
        }
    }

    public static class JsonSerializerExtensions
    {
        public static void Serialize<T>(this T value, string path)
        {
            Json.Serialize(path, value);
        }
    }
}
