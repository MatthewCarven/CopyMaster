namespace CopyMaster;

/// <summary>
/// CopyMaster's icon (assets\CopyMaster.ico, made by dev\make-icons.cs) for the windows' title bars and the
/// taskbar. The exe carries it too (ApplicationIcon), for Explorer; this copy, embedded whole, keeps every
/// size, so the title bar gets a crisp 16 px picture rather than a shrunk 32.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon> s_icon = new(Load);

    public static Icon Value => s_icon.Value;

    private static Icon Load()
    {
        using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("CopyMaster.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
