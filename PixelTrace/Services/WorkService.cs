using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PixelTrace.Mobile;

[Service(Name = AppIdentity.WorkServiceClass, Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class WorkService : Service
{
    private const string Channel = "local-index";
    private const int NotificationId = 10;
    private bool running;
    private bool destroyed;
    private PowerManager.WakeLock? wake;
    public override IBinder? OnBind(Intent? intent)
        => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "cancel")
        {
            App.State.Cancel();
            if (!running) { StopSelf(startId); }
            return StartCommandResult.NotSticky;
        }

        if (running)
        {
            return StartCommandResult.NotSticky;
        }

        running = true;
        try
        {
            NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "작업 진행", NotificationImportance.Low));
            StartForeground(NotificationId, BuildNotification(App.State.Operation));
        }
        catch (Exception error)
        {
            running = false;
            global::Android.Util.Log.Error(AppIdentity.LogTag, error.ToString());
            StopSelf();
            App.State.CompleteWork(false, error);
            return StartCommandResult.NotSticky;
        }

        _ = RunAsync();
        return StartCommandResult.NotSticky;
    }

    private async Task RunAsync()
    {
        AppState state = App.State;
        bool success = false;
        Exception? failure = null;
        long last = 0L;
        void Update()
        {
            if (!Volatile.Read(ref running))
            {
                return;
            }

            if (state.Operation.CanCancel && System.Environment.TickCount64 - last < 1000)
            {
                return;
            }

            last = System.Environment.TickCount64;
            try
            {
                ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, BuildNotification(state.Operation));
            }
            catch (Java.Lang.SecurityException error)
            {
                global::Android.Util.Log.Warn(AppIdentity.LogTag, "진행 알림을 갱신하지 못했습니다: " + error.Message);
            }
        }

        state.Changed += Update;
        try
        {
            PowerManager power = (PowerManager)GetSystemService(PowerService)!;
            wake = power.NewWakeLock(WakeLockFlags.Partial, AppIdentity.WakeLockTag)!;
            wake.Acquire();
            success = await state.ExecuteWorkAsync();
        }
        catch (Exception error)
        {
            // This is the service's asynchronous entry boundary. Exceptions in
            // notification/wake-lock setup must not leave an unobserved task.
            global::Android.Util.Log.Error(AppIdentity.LogTag, error.ToString());
            failure = error;
        }
        finally
        {
            state.Changed -= Update;
            ReleaseWakeLock();
            running = false;
            if (!destroyed)
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
            state.CompleteWork(success, failure);
        }
    }

    public override void OnDestroy()
    {
        destroyed = true;
        if (running)
        {
            App.State.Cancel();
        }

        ReleaseWakeLock();
        base.OnDestroy();
    }

    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        // Android 15+ limits dataSync foreground services. Persisted per-file
        // checkpoints remain available when the user starts the work again.
        App.State.Cancel();
        running = false;
        ReleaseWakeLock();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void ReleaseWakeLock()
    {
        PowerManager.WakeLock? previous = wake;
        wake = null;
        if (previous is null)
        {
            return;
        }

        using (previous)
        {
            if (previous.IsHeld)
            {
                previous.Release();
            }
        }
    }

    private Notification BuildNotification(OperationState operation)
    {
        Intent launch = new(this, typeof(MainActivity));
        PendingIntent open = PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Intent cancelIntent = new Intent(this, typeof(WorkService)).SetAction("cancel");
        PendingIntent cancel = PendingIntent.GetService(this, 1, cancelIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Notification.Builder builder = new Notification.Builder(this, Channel).SetContentTitle(AppIdentity.Name)!.SetContentText(operation.Text)!.SetSmallIcon(Resource.Drawable.ic_notification).SetContentIntent(open)!.SetOnlyAlertOnce(true)!.SetOngoing(true)!.SetCategory(Notification.CategoryProgress)!.SetProgress(operation.Total, operation.Done, operation.Total <= 0)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        }
        if (operation.Total > 0)
        {
            builder.SetSubText($"{operation.Done:N0} / {operation.Total:N0}");
        }
        if (operation.CanCancel)
        {
            builder.AddAction(new Notification.Action.Builder(null, "취소", cancel).Build());
        }
        return builder.Build();
    }
}
