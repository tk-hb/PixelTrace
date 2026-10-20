namespace PixelTrace.Mobile;

internal static class AppIdentity
{
    public const string Name = "PixelTrace";
    public const string Monogram = "PT";
    public const string LogTag = Name;
    public const string WakeLockTag = Name + ":Work";
    public const string Package = "com.tkhb.pixeltrace";
    public const string ApplicationClass = Package + ".App";
    public const string ActivityClass = Package + ".MainActivity";
    public const string WorkServiceClass = Package + ".WorkService";
}
