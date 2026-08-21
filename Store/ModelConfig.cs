using WTangent.Server.Session;

namespace WTangent.Server.Store;

/// <summary>模型配置：缓存读写 + 部分替换（run/serve 共用）</summary>
internal static class ModelConfig
{
    /// <summary>解析模型：读缓存作基底，传入的参数覆盖对应字段，结果写回缓存。返回 null 表示失败。</summary>
    public static ProviderConfig? ResolveProvider(string? url, string? apiKey, string? model, string? variants = null)
    {
        var given = new[] { url, apiKey, model }.Count(v => v is { Length: > 0 });

        var cached = ConfigStore.LoadActive();
        if (cached == null)
        {
            if (given == 0)
            {
                Console.Error.WriteLine("无模型缓存，请先配置（agent run --base-url --key --model 或 TUI 内 /connect）");
                return null;
            }
        }
        else
        {
            // 部分替换：缺省字段用缓存值
            url ??= cached.BaseUrl;
            apiKey ??= cached.ApiKey;
            model ??= cached.Model;
            variants ??= cached.Variants;
        }

        var provider = new ProviderConfig
        {
            Name = url!,
            BaseUrl = url!,
            ApiKey = apiKey ?? "",
            DefaultModel = model ?? "",
            Variants = variants ?? "Default",
        };
        // 写回缓存（缺字段用新值填，不写空）
        var cfg = ConfigStore.Load();
        ConfigStore.Save(cfg with
        {
            Active = url!,
            Providers =
            [
                .. cfg.Providers.Where(p => p.Name != url),
                new ProviderEntry { Name = url!, BaseUrl = url ?? "", Model = model ?? "", ApiKey = apiKey ?? "", Variants = variants ?? "Default" }
            ]
        });
        if (given > 0) Console.WriteLine($"[model] 已缓存: {url} / {model} / variants={variants ?? "Default"}");
        return provider;
    }
}
