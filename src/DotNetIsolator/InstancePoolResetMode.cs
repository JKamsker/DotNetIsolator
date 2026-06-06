namespace DotNetIsolator;

public enum InstancePoolResetMode
{
    /// <summary>
    /// Restores only the pages that changed during runtime startup. This is the fastest reset mode,
    /// but it assumes trusted guest code because bytes written by previous user code into other pages
    /// are not scrubbed.
    /// </summary>
    FastTrustedChangedPages = 0,

    /// <summary>
    /// Restores every initialized page captured in the runtime memory snapshot before reusing a
    /// pooled instance.
    /// </summary>
    FullSnapshotRestore = 1,
}
