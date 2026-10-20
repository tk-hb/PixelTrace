using Android.App;
using Android.Content;
using Android.Runtime;

namespace PixelTrace.Mobile;

[Application(Name = AppIdentity.ApplicationClass)]
public sealed class App(IntPtr handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    public static AppState State { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        State = new AppState(this);
    }
}
