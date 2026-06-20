namespace Core.Abstractions;

// 网络层抽象。骨架阶段为占位,方法签名最小化。
public interface IApiClient
{
    // 设置服务端基址(如 https://vault.example.com)。
    void SetBaseAddress(string baseUrl);
}
