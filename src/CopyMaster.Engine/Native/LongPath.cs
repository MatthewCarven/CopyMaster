namespace CopyMaster.Engine.Native;

/// <summary>
/// Win32 calls get paths in their <c>\\?\</c> form, so paths longer than 260 characters work.
/// .NET's own file APIs do this themselves; only our P/Invokes need it.
/// </summary>
internal static class LongPath
{
    /// <param name="fullPath">A full, normalized path (from Path.GetFullPath or enumeration).</param>
    public static string Extended(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }
}
