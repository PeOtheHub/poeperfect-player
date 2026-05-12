using Android.App;
using Android.Content.PM;
using Android.OS;

namespace PoePerfect.Player.Android;

[Activity(Theme = "@style/MainTheme.NoActionBar", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ResizeableActivity = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
