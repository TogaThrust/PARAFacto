namespace PARAFactoNative.Services;

/// <summary>
/// Fonctionnalités réservées aux builds locaux « démo » (voir <c>ParafactoLocalDemoBuild</c> dans le .csproj).
/// Les installateurs / mises à jour clients sont compilés sans ce symbole.
/// </summary>
internal static class BuildFeatures
{
#if PARAFACTO_LOCAL_DEMO_BUILD
    public const bool LocalDemoDbTools = true;
#else
    public const bool LocalDemoDbTools = false;
#endif
}
