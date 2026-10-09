namespace Confast.Web.Features.Identity;

public static class AppRoles
{
    public const string Administrator = "Administrator";
    public const string Quality = "Quality";
    public const string Production = "Production";
    public const string ReadOnly = "ReadOnly";
    public const string Root = "Root Administrator Prime";
    public const string AdministratorId = "47cd3d4a-0d66-4acf-8556-4017336798d8";
    public const string QualityId = "9eb9ef78-7737-47a5-89fc-10513d3e9c1b";
    public const string ProductionId = "56b3fc07-e152-42ca-b074-a823900c93b3";
    public const string ReadOnlyId = "1b171cb9-9273-42fc-b790-ea934dbb12b9";
    public const string RootId = "e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6";

    public static readonly IReadOnlyList<string> All =
    [
        Administrator,
        Quality,
        Production,
        ReadOnly
    ];

    public static readonly IReadOnlyList<ApplicationRole> Seeds =
    [
        Create(AdministratorId, Administrator),
        Create(QualityId, Quality),
        Create(ProductionId, Production),
        Create(ReadOnlyId, ReadOnly, SystemRoleKind.Baseline, "ReadOnlyBaseline"),
        Create(RootId, Root, SystemRoleKind.Root, "RootAdministratorPrime")
    ];

    private static ApplicationRole Create(string id, string name,
        SystemRoleKind kind = SystemRoleKind.Ordinary, string? key = null) => new(name)
    {
        Id = id,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id,
        SystemKind = kind,
        SystemKey = key,
        IsEnabled = true
    };
}
