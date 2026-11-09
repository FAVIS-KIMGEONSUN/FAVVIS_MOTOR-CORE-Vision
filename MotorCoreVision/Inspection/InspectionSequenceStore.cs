using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FAVIS
{
    public static class InspectionSequenceStore
    {
        private static readonly string SequenceFilePath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Config",
                "lastSequence.json");

        public static ulong LoadLastSequenceNo()
        {
            EnsureDirectory();
            if (!File.Exists(SequenceFilePath))
            {
                return 0;
            }

            try
            {
                using (var stream = new MemoryStream(
                    Encoding.UTF8.GetBytes(File.ReadAllText(SequenceFilePath, Encoding.UTF8))))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(InspectionSequenceState));
                    var state = serializer.ReadObject(stream) as InspectionSequenceState;
                    return state?.LastSequenceNo ?? 0;
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "마지막 검사 시퀀스 번호를 읽을 수 없습니다.",
                    exception);
            }
        }

        public static void SaveLastSequenceNo(ulong sequenceNo)
        {
            EnsureDirectory();
            string temporaryPath = SequenceFilePath + ".tmp";
            try
            {
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(InspectionSequenceState));
                    serializer.WriteObject(
                        stream,
                        new InspectionSequenceState { LastSequenceNo = sequenceNo });
                    File.WriteAllText(
                        temporaryPath,
                        Encoding.UTF8.GetString(stream.ToArray()),
                        new UTF8Encoding(false));
                }

                if (File.Exists(SequenceFilePath))
                {
                    File.Replace(temporaryPath, SequenceFilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, SequenceFilePath);
                }
            }
            catch (Exception exception)
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw new InvalidOperationException(
                    "검사 시퀀스 번호를 저장할 수 없습니다.",
                    exception);
            }
        }

        private static void EnsureDirectory()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SequenceFilePath));
        }
    }

    public sealed class InspectionSequenceState
    {
        public ulong LastSequenceNo { get; set; }
    }
}
