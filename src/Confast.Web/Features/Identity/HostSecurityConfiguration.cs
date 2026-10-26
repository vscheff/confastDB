using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;

namespace Confast.Web.Features.Identity;

internal static class HostSecurityConfiguration
{
    internal static void AddConfastHostSecurity(this WebApplicationBuilder builder)
    {
        var keyPath = builder.Configuration["DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            var protection = builder.Services.AddDataProtection()
                .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "ConfastDB")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keyPath)));
            if (builder.Configuration.GetValue<bool>("DataProtection:ProtectWithDpapi"))
            {
                if (!OperatingSystem.IsWindows())
                    throw new InvalidOperationException("DPAPI key protection requires Windows.");
                protection.ProtectKeysWithDpapi();
            }
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Preserve the profile-independent local workflow. A configured ring opts
            // Development into exactly the same persistent provider used by other hosts.
            for (var index = builder.Services.Count - 1; index >= 0; index--)
            {
                var descriptor = builder.Services[index];
                if (descriptor.ServiceType == typeof(IDataProtectionProvider)
                    || descriptor.ServiceType == typeof(IKeyManager)
                    || descriptor.ServiceType.FullName == "Microsoft.AspNetCore.DataProtection.KeyManagement.Internal.IKeyRingProvider"
                    || descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.KeyManagement.KeyRingProvider"
                    || descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService")
                    builder.Services.RemoveAt(index);
            }
            builder.Services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        }

        var proxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.ForwardLimit = 1;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in proxies)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
    }
}
