namespace FamilyTree.Web;

public sealed record PrivateTreeModeOptions(bool Enabled, Uri? ApiBaseUri)
{
    public const string EnabledSetting = "FAMILYTREE_PRIVATE_MODE";
    public const string ApiBaseUrlSetting = "FAMILYTREE_API_BASE_URL";

    public static PrivateTreeModeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!bool.TryParse(configuration[EnabledSetting], out var enabled) || !enabled)
            return new(false, null);

        var value = configuration[ApiBaseUrlSetting];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps ||
            !address.IsLoopback ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            address.AbsolutePath is not ("" or "/"))
            throw new InvalidOperationException(
                $"{ApiBaseUrlSetting} must be an HTTPS URL on localhost or a loopback IP.");

        return new(true, new UriBuilder(address) { Path = "/" }.Uri);
    }
}
