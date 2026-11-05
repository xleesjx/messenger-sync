using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace MessengerSync.Platform;

/// <summary>구글 로그인 정보를 윈도우 계정으로 암호화(DPAPI)해서 저장한다. 다른 PC나 다른 윈도우 계정에서는 풀 수 없다.</summary>
public sealed class DpapiDataStore : IDataStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MessengerSync.token.v1");
    private readonly string _dir;

    public DpapiDataStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string PathFor<T>(string key) => Path.Combine(_dir, FileDataStore.GenerateStoredKey(key, typeof(T)) + ".bin");

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor<T>(key), bytes);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var p = PathFor<T>(key);
        if (File.Exists(p)) File.Delete(p);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var p = PathFor<T>(key);
        if (!File.Exists(p)) return Task.FromResult(default(T)!);
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(bytes)));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(default(T)!); // 다른 계정에서 만든 파일 → 로그인 다시
        }
    }

    public Task ClearAsync()
    {
        foreach (var f in Directory.GetFiles(_dir, "*.bin")) File.Delete(f);
        return Task.CompletedTask;
    }
}
