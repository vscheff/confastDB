namespace Confast.Web.Features.Identity;

using Microsoft.AspNetCore.Identity;

public static class AppRoles
{
    public const string Administrator = "Administrator";
    public const string Quality = "Quality";
    public const string Production = "Production";
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlyList<string> All =
    [
        Administrator,
        Quality,
        Production,
        ReadOnly
    ];

    public static readonly IReadOnlyList<IdentityRole> Seeds =
    [
        Create("47cd3d4a-0d66-4acf-8556-4017336798d8", Administrator),
        Create("9eb9ef78-7737-47a5-89fc-10513d3e9c1b", Quality),
        Create("56b3fc07-e152-42ca-b074-a823900c93b3", Production),
        Create("1b171cb9-9273-42fc-b790-ea934dbb12b9", ReadOnly)
    ];

    private static IdentityRole Create(string id, string name) => new(name)
    {
        Id = id,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id
    };
}
