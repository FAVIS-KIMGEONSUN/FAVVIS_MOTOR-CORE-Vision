using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FAVIS
{
    public static class InspectionConfigManager
    {
        public static InspectionConfig Deserialize(string json)
        {
            return Json.Deserialize<InspectionConfig>(json, NormalizeJson);
        }

        public static string Serialize(InspectionConfig configuration)
        {
            return Json.Serialize(configuration);
        }

        private static string NormalizeJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("설정 파일이 비어 있습니다.");
            }

            string value = json.Trim().TrimStart('\uFEFF');
            int objectStart = value.IndexOf('{');
            int objectEnd = value.LastIndexOf('}');
            if (objectStart < 0 || objectEnd < objectStart)
            {
                throw new InvalidOperationException("올바른 JSON 객체를 찾을 수 없습니다.");
            }

            JObject root = JObject.Parse(
                value.Substring(objectStart, objectEnd - objectStart + 1));

            if (root["Grabbers"] == null && root["Cameras"] != null)
            {
                root["Grabbers"] = root["Cameras"];
                root.Remove("Cameras");
            }

            JArray sequences = root["Sequences"] as JArray;
            if (sequences != null)
            {
                foreach (JObject sequence in sequences.OfType<JObject>())
                {
                    if (sequence["ExternalLightBrightness"] == null)
                    {
                        sequence["ExternalLightBrightness"] = 100;
                    }
                }
            }

            root.Remove("Grabber");
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
