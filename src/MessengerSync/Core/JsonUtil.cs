using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessengerSync.Core;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글을 그대로 저장
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? text)
        => string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Options);

    public static T LoadFile<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Warn($"설정 파일을 읽지 못해 기본값을 씁니다: {path} ({ex.Message})");
        }
        return new T();
    }

    /// <summary>임시 파일에 쓴 뒤 바꿔치기해서, 쓰는 도중 꺼져도 파일이 깨지지 않게 한다.</summary>
    public static void SaveFile<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value));
        File.Move(tmp, path, overwrite: true);
    }
}
